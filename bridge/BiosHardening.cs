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
                var bootMenu = settings.FirstOrDefault(s => LooksLikeBootMenuAuthSetting(s.name));
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = true,
                    wmiReachable = err == null,
                    wmiError = err,
                    usbBootSetting = usb.name == null ? null : new { name = usb.name, currentValue = usb.value, possibleValues = usb.possible },
                    bootMenuAuthSetting = bootMenu.name == null ? null : new { name = bootMenu.name, currentValue = bootMenu.value, possibleValues = bootMenu.possible },
                    note = "ב-Lenovo מרבית הדגמים דורשים שתהיה כבר מוגדרת סיסמת Supervisor לפני ששינוי הגדרות אחרות (כמו חסימת USB) יתקבל ב-firmware. " +
                           "לכן מוצע קודם להגדיר סיסמת BIOS, ורק אחר כך לחסום אתחול USB/לנעול את תפריט האתחול.",
                };
            }
            case "hp":
            {
                var settings = HpSettings(out string err);
                var usb = settings.FirstOrDefault(s => LooksLikeUsbBootSetting(s.name));
                var bootMenu = settings.FirstOrDefault(s => LooksLikeBootMenuAuthSetting(s.name));
                var (pwIsSet, pwRequiresPhysical) = HpPasswordState();
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = true,
                    wmiReachable = err == null,
                    wmiError = err,
                    usbBootSetting = usb.name == null ? null : new { name = usb.name, currentValue = usb.value, possibleValues = usb.possible },
                    bootMenuAuthSetting = bootMenu.name == null ? null : new { name = bootMenu.name, currentValue = bootMenu.value, possibleValues = bootMenu.possible },
                    passwordIsSet = pwIsSet,
                    passwordRequiresPhysicalPresence = pwRequiresPhysical,
                    note = "נתמך בעיקר בדגמים עסקיים (EliteBook/EliteDesk/ProBook/Z). במחשבי צרכן (Pavilion/Envy וכו') ייתכן שהממשק אינו חשוף כלל. " +
                        (pwRequiresPhysical == true
                            ? "בדגם הזה HP מסמנת את סיסמת ה-BIOS כ-RequiresPhysicalPresence - בחלק מהדגמים/גרסאות firmware זה חוסם כתיבה מרחוק " +
                              "בפועל, באחרים זה רק תיאור. „הפעלת הקשחה” תנסה בכל זאת ותאמת מול התוצאה האמיתית (לא תדווח הצלחה בלי אישור)."
                            : "לא זוהתה הגבלת נוכחות-פיזית על סיסמת ה-BIOS בדגם הזה, אבל זה לא מבטיח הצלחה - ר' תוצאת /bios/harden בפועל."),
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
        bool wantBootMenuAuth = b.Bool("lockBootMenu");
        string newBios = b.Str("biosPassword") ?? "";
        if (!wantPassword && !wantUsbBlock && !wantBootMenuAuth)
            throw BridgeException.BadRequest("יש לבחור לפחות פעולה אחת: הגדרת סיסמת BIOS, חסימת אתחול USB, או נעילת תפריט האתחול");
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
        if (wantBootMenuAuth)
        {
            var r = vendor == "lenovo" ? LenovoLockBootMenu() : HpLockBootMenu();
            steps.Add(new { id = "bootMenuAuth", attempted = r.attempted, success = r.success, settingName = r.settingName, message = r.message });
            Config.Log("bios harden: bootMenuAuth vendor=" + vendor + " setting=" + r.settingName + " success=" + r.success + " msg=" + r.message);
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

    /// <summary>
    /// הגדרה שדורשת סיסמת מנהל כדי בכלל להיכנס לתפריט האתחול (F9/F12 וכו') - נמצאה בפועל על HP EliתeBook
    /// 840 G8 בשם "Prompt for Admin authentication on F9 (Boot Menu)". חסימה חזקה יותר מחסימת USB בלבד:
    /// סוגרת גישה לכל תפריט האתחול, לא רק ל-USB. שם ההגדרה משתנה בין דגמים, אז חיפוש גמיש גם כאן.
    /// </summary>
    private static readonly string[] BootMenuAuthPhrases =
    {
        "admin authentication on f9", "admin authentication on f12", "boot menu authentication",
        "require password for boot menu", "password for f9", "password for f12",
    };

    private static bool LooksLikeBootMenuAuthSetting(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (BootMenuAuthPhrases.Any(n.Contains)) return true;
        bool hasBootMenu = n.Contains("boot menu") || n.Contains("f9") || n.Contains("f12");
        bool hasAuth = n.Contains("admin") || n.Contains("authentication") || n.Contains("password");
        return hasBootMenu && hasAuth;
    }

    /// <summary>מערך הערכים האפשריים לערך "חסום/כבוי" (disable/off/no/...); null אם לא ברור</summary>
    private static readonly string[] DisableWords = { "disable", "disabled", "off", "no", "block", "blocked" };
    /// <summary>מערך הערכים האפשריים לערך "מופעל" (enable/on/yes/require/...); null אם לא ברור</summary>
    private static readonly string[] EnableWords = { "enable", "enabled", "on", "yes", "require", "prompt" };

    private static string PickValue(string[] possible, string[] words)
    {
        if (possible == null) return null;
        foreach (string p in possible)
        {
            string low = (p ?? "").ToLowerInvariant();
            if (words.Any(low.Contains)) return p;
        }
        return null;
    }

    private static string PickDisableValue(string[] possible) => PickValue(possible, DisableWords);
    private static string PickEnableValue(string[] possible) => PickValue(possible, EnableWords);

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

    private static (bool attempted, bool success, string settingName, string message) LenovoBlockUsbBoot() =>
        LenovoApplySetting(LooksLikeUsbBootSetting, PickDisableValue,
            "לא נמצאה במחשב זה הגדרת BIOS שקשורה לאתחול מהתקן USB/חיצוני. ייתכן שהדגם קורא לה בשם אחר, " +
                "או שאין הגדרה כזו בדגם הזה — יש לבדוק ידנית בתפריט ה-BIOS",
            "חוסם אתחול");

    private static (bool attempted, bool success, string settingName, string message) LenovoLockBootMenu() =>
        LenovoApplySetting(LooksLikeBootMenuAuthSetting, PickEnableValue,
            "לא נמצאה במחשב זה הגדרת BIOS שדורשת סיסמת מנהל לכניסה לתפריט האתחול (F9/F12). ייתכן שהדגם קורא לה בשם אחר, " +
                "או שאין הגדרה כזו בדגם הזה — יש לבדוק ידנית בתפריט ה-BIOS",
            "דורש סיסמה לתפריט אתחול");

    /// <summary>הלוגיקה המשותפת: מוצא הגדרה לפי matcher, בוחר ערך לפי valuePicker, כותב ושומר (Lenovo דורש שלב שמירה נפרד)</summary>
    private static (bool attempted, bool success, string settingName, string message) LenovoApplySetting(
        Func<string, bool> matcher, Func<string[], string> valuePicker, string notFoundMsg, string kindLabel)
    {
        var settings = LenovoSettings(out string err);
        if (err != null) return (false, false, null, "קריאת הגדרות ה-BIOS נכשלה: " + err);
        var setting = settings.FirstOrDefault(s => matcher(s.name));
        if (setting.name == null) return (false, false, null, notFoundMsg);
        string newValue = valuePicker(setting.possible);
        if (newValue == null)
            return (false, false, setting.name, "נמצאה הגדרה \"" + setting.name + "\" אך לא ניתן לזהות אוטומטית איזה מהערכים האפשריים (" +
                string.Join(", ", setting.possible ?? Array.Empty<string>()) + ") " + kindLabel + " — יש להגדיר ידנית בתפריט ה-BIOS");

        try
        {
            string setText = "";
            using (var setCls = new ManagementClass(@"root\wmi:Lenovo_SetBiosSetting"))
                foreach (ManagementObject o in setCls.GetInstances())
                {
                    using (o) { setText = o.InvokeMethod("SetBiosSetting", new object[] { setting.name + "," + newValue }) as string ?? ""; }
                    break;
                }
            bool setOk = setText.Equals("Success", StringComparison.OrdinalIgnoreCase);
            if (!setOk)
                return (true, false, setting.name, "שינוי ההגדרה \"" + setting.name + "\" נכשל: " + (string.IsNullOrEmpty(setText) ? "תשובה לא ידועה" : setText) +
                    " — בדגמי Lenovo רבים נדרשת סיסמת Supervisor מוגדרת מראש לפני שינוי הגדרות דרך WMI");

            string saveText = "";
            using (var saveCls = new ManagementClass(@"root\wmi:Lenovo_SaveBiosSettings"))
                foreach (ManagementObject o in saveCls.GetInstances())
                {
                    using (o) { saveText = o.InvokeMethod("SaveBiosSettings", null) as string ?? ""; }
                    break;
                }
            bool saveOk = saveText.Equals("Success", StringComparison.OrdinalIgnoreCase);
            return (true, saveOk, setting.name, saveOk
                ? "ההגדרה \"" + setting.name + "\" הוגדרה ל-\"" + newValue + "\" ונשמרה. השינוי ייכנס לתוקף מההפעלה הבאה"
                : "ההגדרה שונתה אבל השמירה (SaveBiosSettings) נכשלה: " + saveText);
        }
        catch (Exception ex)
        {
            Config.Log("bios lenovo apply-setting: " + ex.Message);
            return (true, false, setting.name, "שינוי ההגדרה נכשל: " + ex.Message);
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

    /// <summary>
    /// true/false/null (לא ידוע) אם "Setup Password" כבר מוגדרת בפועל, לפי HP_BIOSPassword.IsSet - מקור
    /// האמת היחיד (בניגוד לקוד התשובה מ-SetBIOSSetting, שעלול לדווח "הצלחה" גם כשכלום לא קרה בפועל -
    /// ר' ההערה ב-HpSetPassword). גם RequiresPhysicalPresence זמין כאן למי שרוצה אותו למידע בלבד.
    /// </summary>
    private static (bool? isSet, bool? requiresPhysicalPresence) HpPasswordState()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT IsSet, RequiresPhysicalPresence FROM HP_BIOSPassword WHERE Name=\"Setup Password\"");
            foreach (ManagementObject o in s.Get())
                using (o)
                    return (Convert.ToInt32(o["IsSet"]) != 0, Convert.ToInt32(o["RequiresPhysicalPresence"]) != 0);
        }
        catch (Exception ex) { Config.Log("bios hp password state: " + ex.Message); }
        return (null, null);
    }

    /// <summary>
    /// מנסה תמיד בפועל (לא מוותרים מראש על בסיס RequiresPhysicalPresence בלבד - יש דגמים/גרסאות
    /// firmware שבהם זה בסך הכול תיאור של ההגדרה ולא חסימה מוחלטת של הכתיבה), ומוודא את התוצאה
    /// האמיתית מול HP_BIOSPassword.IsSet אחרי הקריאה - כי קוד התשובה של SetBIOSSetting לבדו לא אמין:
    /// אומת בפועל על HP EliteBook 840 G8 שהקריאה יכולה "להצליח" (קוד 0) בעוד IsSet נשאר 0.
    /// </summary>
    private static (bool attempted, bool success, string message) HpSetPassword(string newBiosPassword)
    {
        var (wasSet, requiresPhysical) = HpPasswordState();
        try
        {
            // ManagementClass(...).GetInstances() החזיר ריק אצל כמה משתמשים גם כש-root\hp\instrumentedBIOS
            // בהחלט זמין (ר' HpSettings למטה, שקוראת מאותו namespace בהצלחה) - ManagementObjectSearcher עם
            // שאילתת WQL הוא הדפוס שכבר מוכח עובד כאן, אז משתמשים באותו דפוס גם לכתיבה
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT * FROM HP_BIOSSettingInterface");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    // סדר הפרמטרים האמיתי של HP_BIOSSettingInterface.SetBIOSSetting הוא
                    // (Name, Password-נוכחית, Value-חדש) - לא (Name, Value, Password) כפי שהיה כאן בטעות
                    // (אומת מול הגדרת המחלקה: Get-CimClass ...| select CimClassMethods['SetBIOSSetting'].Parameters).
                    // סיסמה חדשה בקידוד Unicode עוטפים ב-"<utf-16/>"; בלי סיסמה נוכחית מוגדרת משאירים ריק —
                    // אם כבר מוגדרת סיסמה אחרת (לא ידועה לנו) הקריאה נכשלת (Access Denied) ולא דורסת אותה
                    object[] args = { "Setup Password", "", "<utf-16/>" + newBiosPassword };
                    object res = o.InvokeMethod("SetBIOSSetting", args);
                    uint code = Convert.ToUInt32(res);
                    if (code != 0)
                        return (true, false, "הגדרת סיסמת ה-BIOS נכשלה (קוד " + code + "): " + HpErrorText(code) +
                            " — ייתכן שכבר מוגדרת סיסמה אחרת (לא נדרסת בלי לדעת אותה)");
                    // קוד 0 לבדו לא מספיק - מוודאים מול IsSet שבאמת קרה משהו (ר' הערת המתודה)
                    var (nowSet, _) = HpPasswordState();
                    if (nowSet == true) return (true, true, "סיסמת ה-BIOS הוגדרה בהצלחה");
                    if (nowSet == false)
                        return (true, false, "הקריאה החזירה קוד הצלחה, אבל הסיסמה לא נקבעה בפועל (אומת מול HP_BIOSPassword.IsSet)" +
                            (requiresPhysical == true ? " — ככל הנראה בגלל RequiresPhysicalPresence בדגם הזה" : "") +
                            ". יש להגדיר ידנית: כניסה ל-BIOS (F10 באתחול) ← Security ← Setup Password.");
                    // לא הצלחנו לקרוא IsSet בכלל (nowSet == null) - מדווחים לפי קוד התשובה בלבד, בלי להמציא ודאות
                    return (true, true, "סיסמת ה-BIOS הוגדרה כנראה בהצלחה (קוד 0) - לא ניתן היה לאמת מול IsSet, מומלץ לבדוק ב-F10");
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

    private static (bool attempted, bool success, string settingName, string message) HpBlockUsbBoot() =>
        HpApplySetting(LooksLikeUsbBootSetting, PickDisableValue,
            "לא נמצאה במחשב זה הגדרת BIOS שקשורה לאתחול מהתקן USB/חיצוני. ייתכן שהדגם קורא לה בשם אחר, " +
                "או שהיא אינה חשופה ב-WMI בדגם הזה — יש לבדוק ידנית בתפריט ה-BIOS (F10 באתחול)",
            "חוסם אתחול");

    private static (bool attempted, bool success, string settingName, string message) HpLockBootMenu() =>
        HpApplySetting(LooksLikeBootMenuAuthSetting, PickEnableValue,
            "לא נמצאה במחשב זה הגדרת BIOS שדורשת סיסמת מנהל לכניסה לתפריט האתחול (F9/F12). ייתכן שהדגם קורא לה בשם אחר, " +
                "או שהיא אינה חשופה ב-WMI בדגם הזה — יש לבדוק ידנית בתפריט ה-BIOS (F10 באתחול). " +
                "אומת בפועל ש-HP EliteBook 840 G8 חושפת הגדרה כזו תחת השם \"Prompt for Admin authentication on F9 (Boot Menu)\".",
            "דורש סיסמה לתפריט אתחול");

    private static (bool attempted, bool success, string settingName, string message) HpApplySetting(
        Func<string, bool> matcher, Func<string[], string> valuePicker, string notFoundMsg, string kindLabel)
    {
        var settings = HpSettings(out string err);
        if (err != null) return (false, false, null, "קריאת הגדרות ה-BIOS נכשלה: " + err);
        var setting = settings.FirstOrDefault(s => matcher(s.name));
        if (setting.name == null) return (false, false, null, notFoundMsg);
        string newValue = valuePicker(setting.possible);
        if (newValue == null)
            return (false, false, setting.name, "נמצאה הגדרה \"" + setting.name + "\" אך לא ניתן לזהות אוטומטית איזה מהערכים האפשריים (" +
                string.Join(", ", setting.possible ?? Array.Empty<string>()) + ") " + kindLabel + " — יש להגדיר ידנית בתפריט ה-BIOS");

        try
        {
            // ר' הערה זהה ב-HpSetPassword: ManagementObjectSearcher הוא הדפוס המוכח-עובד כאן, לא GetInstances()
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT * FROM HP_BIOSSettingInterface");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    // סדר הפרמטרים האמיתי הוא (Name, Password-נוכחית, Value-חדש) - ר' הערה ב-HpSetPassword
                    object[] args = { setting.name, "", newValue };
                    object res = o.InvokeMethod("SetBIOSSetting", args);
                    uint code = Convert.ToUInt32(res);
                    bool ok = code == 0;
                    return (true, ok, setting.name, ok
                        ? "ההגדרה \"" + setting.name + "\" שונתה ל-\"" + newValue + "\". השינוי ייכנס לתוקף מההפעלה הבאה"
                        : "שינוי ההגדרה נכשל (קוד " + code + "): " + HpErrorText(code));
                }
            }
            return (false, false, setting.name, "לא נמצא ממשק HP_BIOSSettingInterface במחשב זה");
        }
        catch (Exception ex)
        {
            Config.Log("bios hp apply-setting: " + ex.Message);
            return (true, false, setting.name, "שינוי ההגדרה נכשל: " + ex.Message);
        }
    }
}
