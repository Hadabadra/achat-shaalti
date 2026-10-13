using System.Management;
using System.Text.Json;
using System.Linq;

namespace OtzariaBridge;

/// <summary>
/// הקשחת BIOS/UEFI מתוך תוכנה: הגדרת סיסמת BIOS, וחיפוש והפעלה של הגדרה שחוסמת אתחול מהתקן USB/חיצוני.
/// אין ב-Windows ממשק גנרי לכתיבה ל-BIOS — רק ממשקי WMI ספציפיים ליצרן, שכל יצרן בוחר אם לחשוף ואיך:
///
///   - Lenovo: root\wmi, Lenovo_BiosSetting/Lenovo_SetBiosSetting/Lenovo_SaveBiosSettings/Lenovo_SetBiosPassword.
///     מתועד ועובד ברוב דגמי ThinkPad/ThinkCentre, כולל בלי תוכנה נוספת מותקנת.
///   - HP: root\hp\instrumentedBIOS, HP_BIOSEnumeration (קריאה) ו-HP_BIOSSettingInterface.SetBIOSSetting (כתיבה).
///     עובד נייטיב בדגמים עסקיים (EliteBook/EliteDesk/ProBook/Z); לא בטוח בדגמי צרכן.
///   - Dell: אין API מתועד בלי Dell Command | Monitor מותקן בנפרד — לא ממומש, רק זיהוי+הסבר.
///   - Asus: אין API צרכני כלל (רק ACT, עסקי בלבד, Intel Gen12+ 2022 ואילך) — לא ממומש, רק זיהוי+הסבר.
///
/// כל פעולה מדווחת בכנות: attempted/success/message. לעולם לא "הצלחה" בלי אישור בפועל מה-WMI.
/// ר' docs/SECURITY_NOTES.md לפירוט מלא של המגבלות (כולל סיכון סיסמה שנשכחה).
/// </summary>
public static class BiosHardening
{
    public static void Register()
    {
        Server.Route("/bios/status", _ => Status());
        Server.Route("/bios/harden", Harden);
    }

    // ---------- זיהוי יצרן (Win32_ComputerSystem, כמו שאר הגשר משתמש ב-WMI — ר' Programs.cs/SysApi.cs) ----------

    private static (string vendor, string raw, string model) Identify()
    {
        string manu = "", model = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    manu = (o["Manufacturer"] as string ?? "").Trim();
                    model = (o["Model"] as string ?? "").Trim();
                }
        }
        catch (Exception ex) { Config.Log("bios: זיהוי יצרן נכשל: " + ex.Message); }

        string low = manu.ToLowerInvariant();
        string vendor =
            low.Contains("lenovo") ? "lenovo" :
            (low.Contains("hewlett") || low.StartsWith("hp")) ? "hp" :
            low.Contains("dell") ? "dell" :
            (low.Contains("asus") || low.Contains("asustek")) ? "asus" :
            "other";
        return (vendor, manu, model);
    }

    // ---------- /bios/status: זיהוי + מה שזמין בפועל במחשב הזה, בלי לשנות כלום ----------

    private static object Status()
    {
        var (vendor, raw, model) = Identify();
        switch (vendor)
        {
            case "lenovo":
            {
                var settings = LenovoSettings(out string err);
                var usb = settings.FirstOrDefault(s => LooksLikeUsbBootSetting(s.name));
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = true,
                    wmiReachable = err == null,
                    wmiError = err,
                    usbBootSetting = usb.name == null ? null : new { name = usb.name, currentValue = usb.value, possibleValues = usb.possible },
                    note = "ב-Lenovo מרבית הדגמים דורשים שתהיה כבר מוגדרת סיסמת Supervisor לפני ששינוי הגדרות אחרות (כמו חסימת USB) יתקבל ב-firmware. " +
                           "לכן מוצע קודם להגדיר סיסמת BIOS, ורק אחר כך לחסום אתחול USB.",
                };
            }
            case "hp":
            {
                var settings = HpSettings(out string err);
                var usb = settings.FirstOrDefault(s => LooksLikeUsbBootSetting(s.name));
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = true,
                    wmiReachable = err == null,
                    wmiError = err,
                    usbBootSetting = usb.name == null ? null : new { name = usb.name, currentValue = usb.value, possibleValues = usb.possible },
                    note = "נתמך בעיקר בדגמים עסקיים (EliteBook/EliteDesk/ProBook/Z). במחשבי צרכן (Pavilion/Envy וכו') ייתכן שהממשק אינו חשוף כלל.",
                };
            }
            case "dell":
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = false,
                    reason = "ל-Dell אין ממשק WMI מתועד להגדרת סיסמת BIOS/חסימת אתחול USB, בלי התקנה נפרדת של Dell Command | Monitor " +
                             "(תוכנת Dell נוספת). אם היא כבר מותקנת, אפשר להגדיר זאת דרכה; אחרת — ידנית בתפריט ה-BIOS (F2 באתחול).",
                };
            case "asus":
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = false,
                    reason = "ל-Asus אין ממשק תוכנתי צרכני כלל. קיים \"ASUS Configuration Tool\" (ACT) רק למחשבים עסקיים מדור Intel Gen12 " +
                             "ומעלה (2022 ואילך) — לא רלוונטי לרוב המחשבים הביתיים. יש להגדיר ידנית בתפריט ה-BIOS (Del/F2 באתחול).",
                };
            default:
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = false,
                    reason = string.IsNullOrEmpty(raw)
                        ? "לא הצלחנו לזהות את יצרן המחשב."
                        : "היצרן (" + raw + ") אינו אחד היצרנים הנתמכים (Lenovo/HP) להקשחה אוטומטית.",
                };
        }
    }

    // ---------- /bios/harden: הפעולה בפועל (דורש סיסמת מנהל + 2FA, כמו FxPolicy.AdminGate) ----------

    private static object Harden(JsonElement b)
    {
        if (!Kiosk.PasswordConfigured)
            throw new BridgeException("no_password", "יש להגדיר סיסמת נעילה לפני ניסיון הקשחת BIOS", 400);
        string pw = b.Str("password");
        Kiosk.RequirePasswordOrRecovery(pw);
        if (!Kiosk.IsRecoveryCode(pw)) TwoFactor.Require("הקשחת BIOS/UEFI");

        bool wantPassword = b.Bool("setPassword");
        bool wantUsbBlock = b.Bool("blockUsbBoot");
        string newBios = b.Str("biosPassword") ?? "";
        if (!wantPassword && !wantUsbBlock)
            throw BridgeException.BadRequest("יש לבחור לפחות פעולה אחת: הגדרת סיסמת BIOS או חסימת אתחול USB");
        if (wantPassword && newBios.Length < 4)
            throw BridgeException.BadRequest("סיסמת ה-BIOS חייבת להכיל לפחות 4 תווים");

        var (vendor, raw, model) = Identify();
        if (vendor is not ("lenovo" or "hp"))
            throw BridgeException.Unsupported("היצרן (" + (string.IsNullOrEmpty(raw) ? "לא זוהה" : raw) +
                ") אינו נתמך להקשחה אוטומטית. ר' /bios/status להסבר ולאפשרויות הידניות");

        var steps = new List<object>();
        if (wantPassword)
        {
            var r = vendor == "lenovo" ? LenovoSetPassword(newBios) : HpSetPassword(newBios);
            steps.Add(new { id = "password", attempted = r.attempted, success = r.success, message = r.message });
            Config.Log("bios harden: password vendor=" + vendor + " success=" + r.success + " msg=" + r.message);
        }
        if (wantUsbBlock)
        {
            var r = vendor == "lenovo" ? LenovoBlockUsbBoot() : HpBlockUsbBoot();
            steps.Add(new { id = "usbBoot", attempted = r.attempted, success = r.success, settingName = r.settingName, message = r.message });
            Config.Log("bios harden: usbBoot vendor=" + vendor + " setting=" + r.settingName + " success=" + r.success + " msg=" + r.message);
        }
        return new { vendor, manufacturer = raw, model, steps };
    }

    // ---------- זיהוי הגדרת "אתחול מ-USB" בלי להניח שם קבוע (משתנה בין דגמים) ----------

    private static readonly string[] UsbBootPhrases =
    {
        "usb boot", "boot from external", "external device boot", "boot from usb",
        "usb storage boot", "legacy usb boot", "boot external devices", "external boot device",
    };

    private static bool LooksLikeUsbBootSetting(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (UsbBootPhrases.Any(n.Contains)) return true;
        bool hasUsb = n.Contains("usb");
        bool hasExternal = n.Contains("external");
        bool hasBoot = n.Contains("boot");
        return (hasUsb && hasBoot) || (hasExternal && hasBoot);
    }

    /// <summary>מערך הערכים האפשריים לערך "חסום/כבוי" (disable/off/no/...); null אם לא ברור</summary>
    private static readonly string[] DisableWords = { "disable", "disabled", "off", "no", "block", "blocked" };

    private static string PickDisableValue(string[] possible)
    {
        if (possible == null) return null;
        foreach (string p in possible)
        {
            string low = (p ?? "").ToLowerInvariant();
            if (DisableWords.Any(low.Contains)) return p;
        }
        return null;
    }

    // ---------- Lenovo: root\wmi ----------

    /// <summary>כל הגדרות ה-BIOS הנוכחיות (Lenovo_BiosSetting.CurrentSetting), כל שורה בתבנית "שם,ערך,[אפשרות1,אפשרות2,...]"</summary>
    private static List<(string name, string value, string[] possible)> LenovoSettings(out string error)
    {
        error = null;
        var list = new List<(string, string, string[])>();
        try
        {
            using var cls = new ManagementClass(@"root\wmi:Lenovo_BiosSetting");
            foreach (ManagementObject o in cls.GetInstances())
            {
                using (o)
                {
                    object res = o.InvokeMethod("CurrentSetting", null);
                    if (res is string[] lines)
                        foreach (string line in lines)
                        {
                            var parsed = ParseLenovoLine(line);
                            if (parsed.name != null) list.Add(parsed);
                        }
                }
                break; // מופע יחיד בכל מחשב
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Config.Log("bios lenovo enumerate: " + ex.Message);
        }
        return list;
    }

    private static (string name, string value, string[] possible) ParseLenovoLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (null, null, null);
        int b1 = line.IndexOf('[');
        string head = b1 >= 0 ? line[..b1] : line;
        string[] parts = head.Split(',');
        if (parts.Length < 2) return (null, null, null);
        string name = parts[0].Trim();
        string value = parts[1].Trim();
        string[] possible = Array.Empty<string>();
        if (b1 >= 0)
        {
            int b2 = line.IndexOf(']', b1);
            string inner = b2 > b1 ? line[(b1 + 1)..b2] : line[(b1 + 1)..];
            possible = inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }
        return (name.Length == 0 ? null : name, value, possible);
    }

    private static (bool attempted, bool success, string message) LenovoSetPassword(string newBiosPassword)
    {
        try
        {
            using var cls = new ManagementClass(@"root\wmi:Lenovo_SetBiosPassword");
            foreach (ManagementObject o in cls.GetInstances())
            {
                using (o)
                {
                    // "pap" = סיסמת Supervisor/Admin (שולטת גם בהגדרות אתחול). CurrentPassword ריק = אין סיסמה מוגדרת כרגע —
                    // אם כבר מוגדרת סיסמה אחרת (לא ידועה לנו) הקריאה פשוט נכשלת; לא דורסים סיסמה קיימת בלי לדעת אותה
                    string parameter = "pap,," + newBiosPassword + ",ascii,us";
                    object res = o.InvokeMethod("SetBiosPassword", new object[] { parameter });
                    string text = res as string ?? "";
                    bool ok = text.Equals("Success", StringComparison.OrdinalIgnoreCase);
                    return (true, ok, ok
                        ? "סיסמת ה-BIOS הוגדרה בהצלחה"
                        : "הגדרת סיסמת ה-BIOS נכשלה: " + (string.IsNullOrEmpty(text) ? "תשובה לא ידועה מה-firmware" : text) +
                          " — ייתכן שכבר מוגדרת סיסמה אחרת (לא נדרסת בלי לדעת אותה), או שהדגם אינו תומך בפרמטר הזה");
                }
            }
            return (false, false, "לא נמצא ממשק Lenovo_SetBiosPassword במחשב זה");
        }
        catch (Exception ex)
        {
            Config.Log("bios lenovo set-password: " + ex.Message);
            return (true, false, "הגדרת סיסמת ה-BIOS נכשלה: " + ex.Message);
        }
    }

    private static (bool attempted, bool success, string settingName, string message) LenovoBlockUsbBoot()
    {
        var settings = LenovoSettings(out string err);
        if (err != null) return (false, false, null, "קריאת הגדרות ה-BIOS נכשלה: " + err);
        var usb = settings.FirstOrDefault(s => LooksLikeUsbBootSetting(s.name));
        if (usb.name == null)
            return (false, false, null, "לא נמצאה במחשב זה הגדרת BIOS שקשורה לאתחול מהתקן USB/חיצוני. ייתכן שהדגם קורא לה בשם אחר, " +
                "או שאין הגדרה כזו בדגם הזה — יש לבדוק ידנית בתפריט ה-BIOS");
        string disableValue = PickDisableValue(usb.possible);
        if (disableValue == null)
            return (false, false, usb.name, "נמצאה הגדרה \"" + usb.name + "\" אך לא ניתן לזהות אוטומטית איזה מהערכים האפשריים (" +
                string.Join(", ", usb.possible ?? Array.Empty<string>()) + ") חוסם אתחול — יש להגדיר ידנית בתפריט ה-BIOS");

        try
        {
            string setText = "";
            using (var setCls = new ManagementClass(@"root\wmi:Lenovo_SetBiosSetting"))
                foreach (ManagementObject o in setCls.GetInstances())
                {
                    using (o) { setText = o.InvokeMethod("SetBiosSetting", new object[] { usb.name + "," + disableValue }) as string ?? ""; }
                    break;
                }
            bool setOk = setText.Equals("Success", StringComparison.OrdinalIgnoreCase);
            if (!setOk)
                return (true, false, usb.name, "שינוי ההגדרה \"" + usb.name + "\" נכשל: " + (string.IsNullOrEmpty(setText) ? "תשובה לא ידועה" : setText) +
                    " — בדגמי Lenovo רבים נדרשת סיסמת Supervisor מוגדרת מראש לפני שינוי הגדרות דרך WMI");

            string saveText = "";
            using (var saveCls = new ManagementClass(@"root\wmi:Lenovo_SaveBiosSettings"))
                foreach (ManagementObject o in saveCls.GetInstances())
                {
                    using (o) { saveText = o.InvokeMethod("SaveBiosSettings", null) as string ?? ""; }
                    break;
                }
            bool saveOk = saveText.Equals("Success", StringComparison.OrdinalIgnoreCase);
            return (true, saveOk, usb.name, saveOk
                ? "ההגדרה \"" + usb.name + "\" הוגדרה ל-\"" + disableValue + "\" ונשמרה. השינוי ייכנס לתוקף מההפעלה הבאה"
                : "ההגדרה שונתה אבל השמירה (SaveBiosSettings) נכשלה: " + saveText);
        }
        catch (Exception ex)
        {
            Config.Log("bios lenovo block-usb-boot: " + ex.Message);
            return (true, false, usb.name, "שינוי ההגדרה נכשל: " + ex.Message);
        }
    }

    // ---------- HP: root\hp\instrumentedBIOS ----------

    private static List<(string name, string value, string[] possible)> HpSettings(out string error)
    {
        error = null;
        var list = new List<(string, string, string[])>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT Name, Value, PossibleValues FROM HP_BIOSEnumeration");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    string value = o["Value"] as string ?? "";
                    string[] possible = (o["PossibleValues"] as string[]) ?? Array.Empty<string>();
                    if (name.Length > 0) list.Add((name, value, possible));
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Config.Log("bios hp enumerate: " + ex.Message);
        }
        return list;
    }

    private static string HpErrorText(uint code) => code switch
    {
        1 => "לא נתמך בדגם זה",
        2 => "שגיאה לא ידועה",
        3 => "תם הזמן הקצוב",
        4 => "הפעולה נכשלה",
        5 => "פרמטר לא תקין (ייתכן שם הגדרה שגוי)",
        6 => "הגישה נדחתה (ייתכן שסיסמת BIOS אחרת כבר מוגדרת)",
        _ => "קוד תשובה לא מוכר (" + code + ")",
    };

    private static (bool attempted, bool success, string message) HpSetPassword(string newBiosPassword)
    {
        try
        {
            using var cls = new ManagementClass(@"root\hp\instrumentedBIOS:HP_BIOSSettingInterface");
            foreach (ManagementObject o in cls.GetInstances())
            {
                using (o)
                {
                    // תיעוד HP: סיסמה חדשה בקידוד Unicode עוטפים ב-"<utf-16/>"; בלי סיסמה נוכחית מוגדרת משאירים ריק —
                    // אם כבר מוגדרת סיסמה אחרת (לא ידועה לנו) הקריאה נכשלת (Access Denied) ולא דורסת אותה
                    object[] args = { "Setup Password", "<utf-16/>" + newBiosPassword, "" };
                    object res = o.InvokeMethod("SetBIOSSetting", args);
                    uint code = Convert.ToUInt32(res);
                    bool ok = code == 0;
                    return (true, ok, ok
                        ? "סיסמת ה-BIOS הוגדרה בהצלחה"
                        : "הגדרת סיסמת ה-BIOS נכשלה (קוד " + code + "): " + HpErrorText(code) +
                          " — ייתכן שכבר מוגדרת סיסמה אחרת (לא נדרסת בלי לדעת אותה)");
                }
            }
            return (false, false, "לא נמצא ממשק HP_BIOSSettingInterface במחשב זה");
        }
        catch (Exception ex)
        {
            Config.Log("bios hp set-password: " + ex.Message);
            return (true, false, "הגדרת סיסמת ה-BIOS נכשלה: " + ex.Message);
        }
    }

    private static (bool attempted, bool success, string settingName, string message) HpBlockUsbBoot()
    {
        var settings = HpSettings(out string err);
        if (err != null) return (false, false, null, "קריאת הגדרות ה-BIOS נכשלה: " + err);
        var usb = settings.FirstOrDefault(s => LooksLikeUsbBootSetting(s.name));
        if (usb.name == null)
            return (false, false, null, "לא נמצאה במחשב זה הגדרת BIOS שקשורה לאתחול מהתקן USB/חיצוני. ייתכן שהדגם קורא לה בשם אחר, " +
                "או שהיא אינה חשופה ב-WMI בדגם הזה — יש לבדוק ידנית בתפריט ה-BIOS (F10 באתחול)");
        string disableValue = PickDisableValue(usb.possible);
        if (disableValue == null)
            return (false, false, usb.name, "נמצאה הגדרה \"" + usb.name + "\" אך לא ניתן לזהות אוטומטית איזה מהערכים האפשריים (" +
                string.Join(", ", usb.possible ?? Array.Empty<string>()) + ") חוסם אתחול — יש להגדיר ידנית בתפריט ה-BIOS");

        try
        {
            using var cls = new ManagementClass(@"root\hp\instrumentedBIOS:HP_BIOSSettingInterface");
            foreach (ManagementObject o in cls.GetInstances())
            {
                using (o)
                {
                    object[] args = { usb.name, disableValue, "" };
                    object res = o.InvokeMethod("SetBIOSSetting", args);
                    uint code = Convert.ToUInt32(res);
                    bool ok = code == 0;
                    return (true, ok, usb.name, ok
                        ? "ההגדרה \"" + usb.name + "\" שונתה ל-\"" + disableValue + "\". השינוי ייכנס לתוקף מההפעלה הבאה"
                        : "שינוי ההגדרה נכשל (קוד " + code + "): " + HpErrorText(code));
                }
            }
            return (false, false, usb.name, "לא נמצא ממשק HP_BIOSSettingInterface במחשב זה");
        }
        catch (Exception ex)
        {
            Config.Log("bios hp block-usb-boot: " + ex.Message);
            return (true, false, usb.name, "שינוי ההגדרה נכשל: " + ex.Message);
        }
    }
}
