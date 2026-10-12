using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// מצב נעילה (קיוסק).
/// נעילה רכה: סגירת explorer, חסימת מקשים, מעקב שמחזיר את אוצריא לחזית, חסימת מנהל המשימות.
/// נעילה קפדנית: כבויה כברירת מחדל; מופעלת רק כאשר הבקשה כוללת strict:true במפורש.
/// </summary>
public static class Kiosk
{
    private const int Iterations = 200_000;
    private const string PoliciesKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static readonly object Gate = new();
    private static int _failures;
    private static DateTime _lockoutUntil = DateTime.MinValue;
    private static System.Threading.Timer _watchdog;
    private static IntPtr _hook = IntPtr.Zero;
    private static LowLevelKeyboardProc _hookProc;

    public static bool IsActive => Config.Get("kiosk.active", false);
    private static bool HasPassword => !string.IsNullOrEmpty(Config.Get<string>("kiosk.passwordHash", null));

    public static string[] AllowedExtensions => Config.GetList("kiosk.allowOpenExtensions");

    /// <summary>במצב readOnly (ברירת מחדל כבוי) שינוי קבצים חסום בזמן נעילה</summary>
    public static bool ReadOnly => Config.Get("kiosk.readOnly", false);

    /// <summary>חוסם שינוי קבצים בזמן נעילה רק כאשר הופעל מצב readOnly</summary>
    public static void EnsureFileWriteAllowed()
    {
        if (IsActive && ReadOnly) throw BridgeException.Locked("שינוי קבצים חסום במצב נעילה");
    }

    // ---------- אילו הגדרות פתוחות בזמן נעילה ----------

    /// <summary>כל ההגדרות שאפשר לפתוח או לחסום בנעילה. רשת, בלוטות', מצב טיסה, USB ושעה חסומים כברירת מחדל</summary>
    public static readonly string[] AllSections =
        { "audio", "display", "energy", "keyboard", "mouse", "printer", "time", "wifi", "bluetooth", "airplane", "usb" };
    private static readonly string[] DefaultOpenSections = { "audio", "display", "energy", "keyboard", "mouse", "printer" };

    public static string[] OpenSections =>
        Config.HasList("kiosk.openSections") ? Config.GetList("kiosk.openSections") : DefaultOpenSections;

    /// <summary>שער לכל נקודות הקצה של ההגדרות: בזמן נעילה הגדרה שלא נפתחה חסומה גם בגשר, לא רק מוסתרת בתוסף</summary>
    public static void EnsureSectionAllowed(string section)
    {
        if (IsActive && !OpenSections.Contains(section, StringComparer.OrdinalIgnoreCase))
            throw BridgeException.Locked("ההגדרה הזו סגורה במצב נעילה");
    }

    private static readonly (string prefix, string section)[] SectionRoutes =
    {
        ("/sys/audio/", "audio"), ("/sys/display/", "display"), ("/sys/energy/", "energy"),
        ("/sys/keyboard/", "keyboard"), ("/sys/mouse/", "mouse"), ("/sys/printers/", "printer"),
        ("/sys/time/", "time"), ("/sys/wifi/", "wifi"), ("/sys/bluetooth/", "bluetooth"),
        ("/sys/airplane/", "airplane"), ("/sys/usb/", "usb"),
    };

    /// <summary>לאיזו הגדרה שייכת פעולת שינוי. קריאות (get, list וכו') פתוחות תמיד; null = לא שייך להגדרה</summary>
    public static string SectionOf(string path, JsonElement body)
    {
        string p = path.ToLowerInvariant();
        if (p.EndsWith("/get") || p.EndsWith("/list") || p.EndsWith("/devices") || p.EndsWith("/networks")) return null;
        if (p == "/sys/radios/set") return body.Str("kind", "wifi").ToLowerInvariant() == "bluetooth" ? "bluetooth" : "wifi";
        foreach (var (prefix, section) in SectionRoutes)
            if (p.StartsWith(prefix)) return section;
        return null;
    }

    /// <summary>מעבר לרשת אחרת, הדלקת רדיו והצמדת בלוטות' (בעיה #1): חסומים בנעילה אלא אם ההגדרה נפתחה</summary>
    public static void EnsureNetworkAllowed(string section = "wifi") => EnsureSectionAllowed(section);

    /// <summary>
    /// האם המשתמש הנוכחי חבר בקבוצת ה-Administrators המקומית.
    /// זו "נקודת תורפה": משתמש עם הרשאות מנהל יכול לבטל את הנעילה (לעצור את הגשר,
    /// לשנות רישום/מדיניות, או להיכנס כמנהל) — ולכן מומלץ שעמדת לימוד תרוץ בחשבון רגיל.
    /// מזהה חברות בקבוצה גם כאשר הטוקן מסונן על ידי UAC (deny-only group עדיין מופיעה).
    /// </summary>
    public static bool UserIsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            return id.Groups != null && id.Groups.Any(g => g == admins);
        }
        catch { return false; }
    }

    /// <summary>אזהרות על מצב הקשחה חלש, להצגה למי שמפעיל את הנעילה</summary>
    private static string[] Warnings()
    {
        var w = new List<string>();
        if (UserIsAdministrator()) w.Add("admin");
        return w.ToArray();
    }

    public static void Register()
    {
        Server.Route("/kiosk/status", _ => Status());
        Server.Route("/kiosk/set-password", SetPassword);
        Server.Route("/kiosk/enable", Enable);
        Server.Route("/kiosk/disable", Disable);
        Server.Route("/kiosk/settings", Settings);
        Programs.Register();
    }

    // ---------- סיסמה ----------

    private static string Hash(string secret, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32));

    private static bool Matches(string secret, string hashKey, string saltKey)
    {
        string hash = Config.Get<string>(hashKey, null);
        string salt = Config.Get<string>(saltKey, null);
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(secret)) return false;
        byte[] expected = Convert.FromBase64String(hash);
        byte[] actual = Convert.FromBase64String(Hash(secret, Convert.FromBase64String(salt)));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void StoreSecret(string secret, string hashKey, string saltKey)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        Config.Set(saltKey, Convert.ToBase64String(salt));
        Config.Set(hashKey, Hash(secret, salt));
    }

    private static string NormalizeRecovery(string s) => (s ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

    /// <summary>בודק סיסמה או קוד שחזור, עם השהיה אחרי 5 ניסיונות שגויים</summary>
    private static void Verify(string password, bool allowRecovery)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow < _lockoutUntil)
            {
                int secs = (int)Math.Ceiling((_lockoutUntil - DateTime.UtcNow).TotalSeconds);
                throw new BridgeException("wrong_password", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {secs} שניות", 429);
            }
            // כשרכיב ההקשחה מותקן — הוא מקור האמת לסיסמה (עריכת config.json לא עוזרת)
            if (HardLock.Available) { HardLock.Verify(password, allowRecovery); return; }
            bool ok = Matches(password, "kiosk.passwordHash", "kiosk.salt") ||
                      (allowRecovery && Matches(NormalizeRecovery(password), "kiosk.recoveryHash", "kiosk.recoverySalt"));
            if (ok)
            {
                _failures = 0;
                return;
            }
            if (++_failures >= 5)
            {
                _failures = 0;
                _lockoutUntil = DateTime.UtcNow.AddSeconds(60);
            }
            throw new BridgeException("wrong_password", "סיסמה שגויה", 401);
        }
    }

    /// <summary>אימות סיסמה לשימוש מודולים אחרים (בלי קוד שחזור)</summary>
    internal static void RequirePassword(string password) => Verify(password, allowRecovery: false);

    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);

    /// <summary>
    /// מביא חלון לחזית באמת: משחזר חלון מוקטן, ועוקף את חסימת Windows להעברת פוקוס מתהליך רקע
    /// (חיבור חוטי הקלט + לחיצת Alt שקטה + הצמדה זמנית למעלה). בלי זה התוכנה נפתחת מאחור ולא ניתן להשתמש בה.
    /// </summary>
    internal static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        else ShowWindow(hwnd, 5 /* SW_SHOW */);
        uint cur = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint target = GetWindowThreadProcessId(hwnd, out _);
        bool a1 = false, a2 = false;
        try
        {
            // לא מתחברים לחוט של חלון תקוע: זה היה עלול לתקוע גם את הגשר
            if (fgThread != 0 && fgThread != cur && !IsHungAppWindow(fg)) a1 = AttachThreadInput(cur, fgThread, true);
            if (target != 0 && target != cur && !IsHungAppWindow(hwnd)) a2 = AttachThreadInput(cur, target, true);
            // Alt למטה ולמעלה: Windows מתירה אז החלפת חזית. מקש לא מוקצה (0xE8) באמצע מונע מהתוכנה להפעיל את תפריט Alt שלה
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0xE8, 0, 0, UIntPtr.Zero);
            keybd_event(0xE8, 0, 2, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // TOPMOST, בלי שינוי גודל
            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // חזרה ל-NOTOPMOST (נשאר בחזית)
            BringWindowToTop(hwnd);
            if (!SetForegroundWindow(hwnd)) SwitchToThisWindow(hwnd, true);
        }
        catch (Exception ex) { Config.Log("bring to front: " + ex.Message); }
        finally
        {
            if (a1) AttachThreadInput(cur, fgThread, false);
            if (a2) AttachThreadInput(cur, target, false);
        }
    }

    private static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 12).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    // ---------- נקודות קצה ----------

    private static object Status() => new
    {
        active = IsActive,
        hasPassword = HasPassword,
        strict = Config.Get("kiosk.strict", false),
        autoStartBridge = Installer.HasAutoStart(),
        otzariaPath = OtzariaPath() ?? "",
        blockTaskManager = Config.Get("kiosk.blockTaskManager", true),
        allowOpenExtensions = AllowedExtensions,
        readOnly = ReadOnly,
        clock = Config.Get("kiosk.clock", true),
        clockPosition = Config.Get("kiosk.clockPosition", "bottom-left"),
        restScreen = Config.Get("kiosk.restScreen", true),
        fullscreenOtzaria = Config.Get("kiosk.fullscreenOtzaria", true),
        sideBar = Config.Get("kiosk.sideBar", true),
        resetOnShutdown = Config.Get("kiosk.resetOnShutdown", false),
        openSections = OpenSections,
        allSections = AllSections,
        warnings = Warnings(),
        hard = HardLock.Status(),
    };

    /// <summary>רכיב ההקשחה אומר "נעול" (למשל אחרי עריכת config.json או בחשבון Windows אחר) — נועלים גם כאן</summary>
    public static void ForceActivate(string servicePath)
    {
        if (string.IsNullOrEmpty(OtzariaPath()) && !string.IsNullOrEmpty(servicePath) && File.Exists(servicePath))
            Config.Set("kiosk.otzariaPath", servicePath);
        string otz = OtzariaPath();
        if (string.IsNullOrEmpty(otz) || !File.Exists(otz))
        {
            // בלי אוצריא בחשבון הזה לא נועלים (אחרת לא היה נשאר שום ממשק) — רק מתעדים
            LogThrottled("noOtz", "hardlock: נעילה מוקשחת פעילה, אבל אוצריא לא נמצאה בחשבון הזה — הנעילה לא הוחלה", 300);
            return;
        }
        Config.Log("hardlock: הנעילה הוחלה לפי רכיב ההקשחה");
        Config.Set("kiosk.active", true);
        Config.Set("kiosk.confirmed", true);
        Config.Save();
        try { Apply(); } catch (Exception ex) { Config.Log("hardlock apply: " + ex.Message); }
    }

    /// <summary>הנעילה בוטלה ברכיב ההקשחה (למשל בקוד שחזור) — משחררים גם כאן</summary>
    public static void ReleaseFromService()
    {
        Config.Log("hardlock: הנעילה בוטלה ברכיב ההקשחה");
        Release();
    }

    private static object SetPassword(JsonElement b)
    {
        string newPassword = b.Str("newPassword") ?? "";
        if (newPassword.Length < 4) throw BridgeException.BadRequest("הסיסמה חייבת להכיל לפחות 4 תווים");
        string recovery = null;
        if (HasPassword)
        {
            // שינוי סיסמה קיימת: אימות הישנה, וסנכרון לרכיב ההקשחה אם מותקן. קוד השחזור הקיים נשמר
            Verify(b.Str("oldPassword"), allowRecovery: true);
            if (HardLock.Available) HardLock.SetPassword(b.Str("oldPassword"), newPassword);
        }
        else
        {
            // הגדרה ראשונה: יוצרים קוד שחזור חד-פעמי
            recovery = NewRecoveryCode();
            StoreSecret(recovery, "kiosk.recoveryHash", "kiosk.recoverySalt");
        }
        StoreSecret(newPassword, "kiosk.passwordHash", "kiosk.salt");
        Config.Save();
        return recovery == null ? new { ok = true } : new { ok = true, recoveryCode = recovery };
    }

    private static object Enable(JsonElement b)
    {
        if (!HasPassword) throw BridgeException.BadRequest("יש להגדיר סיסמה לפני הפעלת הנעילה");
        Verify(b.Str("password"), allowRecovery: false);

        bool strict = b.Bool("strict", false); // כבוי כברירת מחדל
        bool blockTm = b.Bool("blockTaskManager", true);
        string otz = OtzariaPath();
        if (string.IsNullOrEmpty(otz) || !File.Exists(otz))
            throw BridgeException.BadRequest("לא נמצאה תוכנת אוצריא. יש להגדיר otzariaPath בהגדרות הנעילה");

        Config.Set("kiosk.strict", strict);
        Config.Set("kiosk.blockTaskManager", blockTm);
        Config.Set("kiosk.active", true);
        Config.Save();
        try { Apply(); }
        catch
        {
            // הפעלה שנכשלה לא משאירה את המחשב במצב נעילה חלקי
            Release();
            throw;
        }
        Config.Set("kiosk.confirmed", true);
        Config.Save();
        if (HardLock.Available)
        {
            try { HardLock.Enable(b.Str("password"), otz); }
            catch (Exception ex) { Config.Log("hardlock enable: " + ex.Message); }
        }
        return new { active = true };
    }

    private static object Disable(JsonElement b)
    {
        Verify(b.Str("password"), allowRecovery: true);
        if (HardLock.Available) HardLock.Disable(b.Str("password"));
        Release();
        return new { active = false };
    }

    private static object Settings(JsonElement b)
    {
        Verify(b.Str("password"), allowRecovery: false);
        if (b.Has("readOnly")) Config.Set("kiosk.readOnly", b.Bool("readOnly"));
        foreach (string k in new[] { "clock", "restScreen", "fullscreenOtzaria", "sideBar", "resetOnShutdown" })
            if (b.Has(k)) Config.Set("kiosk." + k, b.Bool(k));
        string cp = b.Str("clockPosition");
        if (cp is "bottom-left" or "bottom-right" or "top-left" or "top-right") Config.Set("kiosk.clockPosition", cp);
        if (b.Has("openSections"))
            Config.Set("kiosk.openSections", new JsonArray(b.StrArray("openSections")
                .Where(x => AllSections.Contains(x, StringComparer.OrdinalIgnoreCase))
                .Select(x => (JsonNode)x.ToLowerInvariant()).ToArray()));
        if (b.Has("allowOpenExtensions"))
            Config.Set("kiosk.allowOpenExtensions",
                new JsonArray(b.StrArray("allowOpenExtensions").Select(x => (JsonNode)x.TrimStart('.').ToLowerInvariant()).ToArray()));
        if (b.Has("otzariaPath"))
        {
            string p = b.Str("otzariaPath", "");
            if (p.Length > 0 && !File.Exists(p)) throw BridgeException.NotFound("הקובץ לא נמצא: " + p);
            Config.Set("kiosk.otzariaPath", p);
        }
        Config.Save();
        if (IsActive) { Apply(); Overlay.ShowSideBar(); }
        return Status();
    }

    // ---------- איתור אוצריא ----------

    public static string OtzariaPath()
    {
        string configured = Config.Get<string>("kiosk.otzariaPath", null);
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        try
        {
            foreach (var p in Process.GetProcessesByName("otzaria"))
            {
                string path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(local, "Programs", "otzaria", "otzaria.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "otzaria", "otzaria.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "otzaria", "otzaria.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------- החלה ושחרור ----------

    /// <summary>
    /// הגנת עלייה (בעיה #8, "מסך שחור אחרי הפעלה מחדש"): רצה בתחילת כל הפעלה של הגשר, לפני כל דבר אחר.
    /// אם אין נעילה פעילה ומאושרת, או שהמחשב עלה במצב בטוח — מוחקים כל שארית של הנעילה ברישום
    /// (מעטפת Shell שמפנה לגשר, חסימת מנהל המשימות) ומוודאים ש-explorer רץ, כדי ששולחן העבודה תמיד יעלה.
    /// </summary>
    public static void BootGuard()
    {
        try
        {
            bool safeMode = SystemInformation.BootMode != BootMode.Normal;
            bool locked = ((IsActive && Config.Get("kiosk.confirmed", false)) || HardLock.ServiceActive) && !safeMode;
            if (safeMode && IsActive)
            {
                Config.Log("kiosk: מצב בטוח — הנעילה מבוטלת לעלייה הזו");
                Config.Set("kiosk.active", false);
                Config.Set("kiosk.confirmed", false);
                Config.Save();
            }
            if (locked) return;
            bool hadShell = ShellPointsToBridge();
            ClearRegistry();
            if (hadShell) Config.Log("kiosk: נמצאה מעטפת Shell שמפנה לגשר בלי נעילה פעילה — נמחקה, ו-explorer מופעל");
            // כשהגשר עצמו הופעל כמעטפת אין explorer כלל; בעלייה רגילה (Run) explorer כבר רץ ולא נוגעים בו
            if (hadShell || Process.GetProcessesByName("explorer").Length == 0) StartExplorer();
        }
        catch (Exception ex) { Config.Log("boot guard: " + ex.Message); StartExplorer(); }
    }

    private static bool ShellPointsToBridge()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(WinlogonKey);
            string v = k?.GetValue("Shell") as string;
            return !string.IsNullOrEmpty(v) && v.IndexOf("OtzariaBridge", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    public static void RestoreOnStartup()
    {
        if (!IsActive && HardLock.ServiceActive) { ForceActivate(null); return; }
        if (!IsActive) return;
        // נעילה חוזרת בעלייה רק אם הופעלה בהצלחה. מצב שנשמר מהפעלה שנכשלה (גרסאות 0.0.x שמרו
        // "פעיל" לפני ההחלה) מנוקה, כדי שהפעלת הגשר לא תנעל את המחשב בהפתעה.
        if (!Config.Get("kiosk.confirmed", false))
        {
            Config.Log("kiosk: מצב נעילה לא מאושר מהפעלה קודמת — מנוקה בעלייה");
            Release();
            return;
        }
        try { Apply(); }
        catch (Exception ex) { Config.Log("kiosk restore: " + ex.Message); Release(); }
    }

    private static bool _taskMgrByPolicy;
    private static bool _sessionHooked;

    /// <summary>
    /// אפשרות "ביטול נעילה בכיבוי": כשהמחשב מכבה או המשתמש מתנתק, הנעילה מתבטלת, ובהפעלה הבאה המחשב רגיל.
    /// לא פועל עם נעילה מוקשחת (שם השירות הוא מקור האמת), ולא בסגירה של הגשר עצמו או בעדכון.
    /// </summary>
    private static void HookSessionEnding()
    {
        if (_sessionHooked) return;
        _sessionHooked = true;
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) =>
        {
            try
            {
                if (!IsActive || !Config.Get("kiosk.resetOnShutdown", false) || HardLock.ServiceActive) return;
                Config.Set("kiosk.active", false);
                Config.Set("kiosk.confirmed", false);
                Config.Set("kiosk.strict", false);
                Config.Save();
                ClearRegistry();
                Config.Log("kiosk: הנעילה בוטלה בכיבוי (הוגדר: ביטול נעילה בכיבוי)");
            }
            catch { }
        };
    }

    private static void Apply()
    {
        lock (Gate)
        {
            // חסימת מנהל המשימות דרך מדיניות. במחשבים רבים המפתח Policies\System נעול למשתמש רגיל
            // (נוצר על ידי מנהל/מדיניות קבוצתית), ולכן זו חסימה "במאמץ הטוב": אם אין הרשאה,
            // הנעילה ממשיכה, ומנהל המשימות נסגר על ידי שומר הנעילה ונחסם במקלדת (Ctrl+Shift+Esc).
            if (UserIsAdministrator())
                Config.Log("kiosk: אזהרה — הנעילה הופעלה בחשבון עם הרשאות מנהל, שניתן לעקוף אותה ממנו. מומלץ להפעיל בעמדת לימוד בחשבון משתמש רגיל.");

            _taskMgrByPolicy = false;
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(PoliciesKey);
                if (Config.Get("kiosk.blockTaskManager", true)) { k.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord); _taskMgrByPolicy = true; }
                else k.DeleteValue("DisableTaskMgr", false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Config.Log("kiosk: Policies\\System לא נגיש, חסימת מנהל המשימות בשומר בלבד: " + ex.Message);
            }

            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(WinlogonKey);
                // נעילה קפדנית: המעטפת היא הגשר, שמפעיל את אוצריא. ללא strict המעטפת לא משתנה
                if (Config.Get("kiosk.strict", false))
                    k.SetValue("Shell", "\"" + Installer.InstalledExe + "\" --background", RegistryValueKind.String);
                else k.DeleteValue("Shell", false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Config.Log("kiosk: Winlogon לא נגיש: " + ex.Message);
                if (Config.Get("kiosk.strict", false))
                    throw BridgeException.Forbidden("נעילה קפדנית דורשת הרשאה לשנות את מעטפת Windows, ואין הרשאה כזו במחשב הזה. אפשר להפעיל נעילה רגילה.");
            }

            Tray.Invoke(InstallHook);
            HookSessionEnding();
            Overlay.ShowClock(); // שורת המשימות מוסתרת — שעה ותאריך בשעון צף
            Overlay.ShowSideBar(); // סרגל תוכנות בצד המסך: מעבר מהיר בין אוצריא לתוכנות התורניות
            _watchdog ??= new System.Threading.Timer(_ => Watch(), null, 0, 2000);
            Tray.SetKioskState(true);
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            Config.Set("kiosk.active", false);
            Config.Set("kiosk.confirmed", false);
            Config.Set("kiosk.strict", false);
            Config.Save();
            _watchdog?.Dispose();
            _watchdog = null;
            // ממתינים שסבב שומר שכבר רץ יסתיים, כדי שלא יסגור את explorer מיד אחרי שהופעל מחדש
            for (int i = 0; i < 30 && Volatile.Read(ref _watchBusy) == 1; i++) Thread.Sleep(100);
            Tray.Invoke(RemoveHook);
            Overlay.HideAll();
            RestoreWindowFrame();
            ClearRegistry();
            StartExplorer();
            Tray.SetKioskState(false);
            // בדיקה שנייה אחרי כמה שניות: אם explorer עדיין לא עלה, מפעילים שוב
            System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ => { if (!IsActive) StartExplorer(); });
        }
    }

    private static void ClearRegistry()
    {
        // כל מפתח בנפרד: כישלון במדיניות (מפתח נעול) לא ימנע את מחיקת המעטפת, שהיא הקריטית
        try { using var k = Registry.CurrentUser.OpenSubKey(WinlogonKey, true); k?.DeleteValue("Shell", false); }
        catch (Exception ex) { Config.Log("kiosk clear shell: " + ex.Message); }
        try { using var k = Registry.CurrentUser.OpenSubKey(PoliciesKey, true); k?.DeleteValue("DisableTaskMgr", false); }
        catch (Exception ex) { Config.Log("kiosk clear policy: " + ex.Message); }
        if (ShellPointsToBridge()) Config.Log("kiosk clear: אזהרה — המעטפת עדיין מפנה לגשר");
    }

    private static void StartExplorer()
    {
        try
        {
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                    { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>קוד שחזור משורת הפקודה: OtzariaBridge.exe --unlock CODE</summary>
    public static bool EmergencyUnlock(string code)
    {
        if (!Matches(NormalizeRecovery(code), "kiosk.recoveryHash", "kiosk.recoverySalt")) return false;
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("OtzariaBridge"))
        {
            if (p.Id == self) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        Config.Reload();
        Config.Set("kiosk.active", false);
        Config.Set("kiosk.strict", false);
        Config.Save();
        ClearRegistry();
        StartExplorer();
        return true;
    }

    /// <summary>
    /// קיצור חירום מהגשר הרץ (Ctrl+Alt+Shift+F12): מבטל את הנעילה כולה (גם את השירות) — אבל רק עם קוד השחזור.
    /// קיצור בלי שום אימות היה מאפשר לכל תלמיד לבטל את הנעילה, ולכן הקוד נדרש.
    /// </summary>
    public static bool EmergencyRelease(string code)
    {
        if (!Matches(NormalizeRecovery(code), "kiosk.recoveryHash", "kiosk.recoverySalt")) return false;
        try { HardLock.EmergencyDisable(code); } catch (Exception ex) { Config.Log("emergency: " + ex.Message); }
        Release();
        Config.Log("kiosk: הנעילה בוטלה בקיצור חירום עם קוד שחזור");
        return true;
    }

    /// <summary>ביטול נעילה בזמן הסרה</summary>
    public static string ReleaseForUninstall(string password)
    {
        if (HasPassword && !(Matches(password, "kiosk.passwordHash", "kiosk.salt") ||
                             Matches(NormalizeRecovery(password), "kiosk.recoveryHash", "kiosk.recoverySalt")))
            return "נדרשת סיסמת הנעילה: --password <סיסמה>";
        Config.Set("kiosk.active", false);
        Config.Set("kiosk.strict", false);
        ClearRegistry();
        StartExplorer();
        return null;
    }

    // ---------- מעקב ----------

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static DateTime _lastFit = DateTime.MinValue;
    private static int _fitFails;
    private static DateTime _fitPausedUntil = DateTime.MinValue;

    /// <summary>רושם ליומן הודעה אבחונית לכל היותר פעם בכל כמה שניות לכל מפתח, כדי לא להציף את היומן</summary>
    private static readonly System.Collections.Generic.Dictionary<string, DateTime> _logTimes = new();
    private static void LogThrottled(string key, string msg, int seconds)
    {
        var now = DateTime.UtcNow;
        if (_logTimes.TryGetValue(key, out var t) && (now - t).TotalSeconds < seconds) return;
        _logTimes[key] = now;
        Config.Log(msg);
    }

    /// <summary>
    /// מוודא שחלון אוצריא ממלא את כל המסך. אם הוא לא ממוקסם — ממקסם. אם הוא ממוקסם אבל עדיין משאיר
    /// פס ריק (אזור העבודה לא התעדכן אחרי סגירת שורת המשימות) — מותח את החלון ישירות לגבולות המסך (ואם זה לא עזר,
    /// משחזר וממקסם), לכל היותר פעם ב-8 שניות, ואחרי 3 ניסיונות כושלים רצופים משהה את הנסיון ל-5 דקות (כדי שלא ירצד). סבילות של 16 פיקסלים:
    /// חלון ממוקסם עשוי להיות קטן או גדול במעט מגבולות המסך (מסגרת, DPI) וזה אינו "פס ריק".
    /// חלון במסך מלא של אוצריא עצמה (מכסה את כל המסך) לא נוגעים בו.
    /// </summary>
    private static void FitToScreen(IntPtr hwnd)
    {
        const int SwRestore = 9, SwMaximize = 3, Tol = 16;
        if (!GetWindowRect(hwnd, out var r)) return;
        var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        bool covers = r.Left <= b.Left + Tol && r.Top <= b.Top + Tol && r.Right >= b.Right - Tol && r.Bottom >= b.Bottom - Tol;
        if (covers) { _fitFails = 0; return; }
        if (!IsZoomed(hwnd)) { ShowWindow(hwnd, SwMaximize); return; }
        if (DateTime.UtcNow < _fitPausedUntil) return;
        if ((DateTime.UtcNow - _lastFit).TotalSeconds < 8) return;
        _lastFit = DateTime.UtcNow;
        if (++_fitFails > 3)
        {
            _fitPausedUntil = DateTime.UtcNow.AddMinutes(5);
            _fitFails = 0;
            Config.Log($"watchdog: מיקסום חוזר לא עזר (חלון {r.Left},{r.Top},{r.Right},{r.Bottom} מול מסך {b.Left},{b.Top},{b.Right},{b.Bottom}); מושהה 5 דקות");
            return;
        }
        LogThrottled("fit", $"watchdog: ממלא את המסך (חלון {r.Left},{r.Top},{r.Right},{r.Bottom} מול מסך {b.Left},{b.Top},{b.Right},{b.Bottom})", 60);
        // קודם מותחים ישירות לגבולות המסך (בלי שחזור-ומקסום שמרצד); רק אם זה לא עזר בניסיון הקודם — שחזור ומקסום
        if (_fitFails <= 1)
            SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, SwpNoZOrder | SwpNoActivate);
        else
        {
            ShowWindow(hwnd, SwRestore);
            ShowWindow(hwnd, SwMaximize);
        }
    }

    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpFrameChanged = 0x0020;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    private static IntPtr _fsHwnd = IntPtr.Zero;
    private static long _fsOrigStyle;

    /// <summary>
    /// מסך מלא אמיתי של אוצריא בנעילה: מסיר כותרת ומסגרת וממלא את כל המסך. אם אוצריא כבר ללא מסגרת
    /// (מסך מלא שלה עצמה) לא נוגעים בה. המסגרת המקורית מוחזרת ביציאה מהנעילה. כיבוי: kiosk.fullscreenOtzaria=false.
    /// </summary>
    private static void MakeFullscreen(IntPtr hwnd)
    {
        const int GwlStyle = -16;
        const long WsCaption = 0xC00000, WsThickFrame = 0x40000;
        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        if ((style & (WsCaption | WsThickFrame)) == 0) return;
        _fsHwnd = hwnd;
        _fsOrigStyle = style;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style & ~(WsCaption | WsThickFrame)));
        var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        LogThrottled("fullscreen", "watchdog: אוצריא עברה למסך מלא (הוסרו כותרת ומסגרת)", 300);
    }

    /// <summary>מחזיר לחלון אוצריא את הכותרת והמסגרת שהוסרו במסך המלא</summary>
    private static void RestoreWindowFrame()
    {
        try
        {
            if (_fsHwnd == IntPtr.Zero || !IsWindow(_fsHwnd)) return;
            SetWindowLongPtr(_fsHwnd, -16, new IntPtr(_fsOrigStyle));
            SetWindowPos(_fsHwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | SwpNoMove | SwpNoSize | SwpFrameChanged);
            ShowWindow(_fsHwnd, 9 /* SW_RESTORE */);
            ShowWindow(_fsHwnd, 3 /* SW_MAXIMIZE */);
        }
        catch (Exception ex) { Config.Log("kiosk restore frame: " + ex.Message); }
        finally { _fsHwnd = IntPtr.Zero; }
    }

    private static int _watchBusy;

    private static void Watch()
    {
        if (!IsActive || Interlocked.Exchange(ref _watchBusy, 1) == 1) return;
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (p.SessionId != session) continue;
                    p.Kill();
                    // אם Windows מרימה את explorer מחדש, שורת המשימות מהבהבת בכל מחזור; היומן יראה את זה
                    LogThrottled("explorer", "watchdog: explorer נסגר (אם ההודעה חוזרת כל דקה — Windows מרימה אותו מחדש)", 60);
                }
                catch { }
            }

            // אם המדיניות לא נכתבה (אין הרשאה) — סוגרים את מנהל המשימות כשהוא נפתח (מה שאפשר בלי הרשאות מנהל)
            if (!_taskMgrByPolicy && Config.Get("kiosk.blockTaskManager", true))
                foreach (var p in Process.GetProcessesByName("Taskmgr"))
                {
                    try { if (p.SessionId == session) p.Kill(); } catch { }
                }

            string path = OtzariaPath();
            string name = string.IsNullOrEmpty(path) ? "otzaria" : Path.GetFileNameWithoutExtension(path);
            var otz = Process.GetProcessesByName(name).FirstOrDefault(p => p.SessionId == session);
            if (otz == null)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
                return;
            }

            IntPtr hwnd = otz.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;

            // אוצריא תמיד ממוקסמת: בלי מזעור, בלי חלון קטן, ובלי חור במקום שורת המשימות
            // אוצריא ממוזערת: מסך מנוחה עם סמל אוצריא, שעה ותאריך, במקום החזרה מיידית
            if (IsIconic(hwnd))
            {
                if (Config.Get("kiosk.restScreen", true)) { Overlay.ShowRest(hwnd); return; }
                ShowWindow(hwnd, 9 /* SW_RESTORE */);
            }
            else if (Overlay.RestVisible) Overlay.HideRest();
            if (Overlay.RestVisible) return;
            if (Config.Get("kiosk.maximizeOtzaria", true)) FitToScreen(hwnd);
            if (Config.Get("kiosk.fullscreenOtzaria", true)) MakeFullscreen(hwnd);

            GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
            if (fgPid == otz.Id || fgPid == Environment.ProcessId) return;
            if (Programs.ShouldYieldTo(fgPid)) return; // תוכנה מותרת (רשימה מוגדרת בסיסמה) בחזית
            try
            {
                using var fgp = Process.GetProcessById((int)fgPid);
                LogThrottled("fg:" + fgp.ProcessName, "watchdog: מחזיר את אוצריא לחזית מהתהליך " + fgp.ProcessName, 60);
            }
            catch { }
            if (!SetForegroundWindow(hwnd)) SwitchToThisWindow(hwnd, true);
        }
        catch (Exception ex) { Config.Log("watchdog: " + ex.Message); }
        finally { Interlocked.Exchange(ref _watchBusy, 0); }
    }

    // ---------- חסימת מקשים ----------

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

    private static void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Config.Log("keyboard hook failed: " + Marshal.GetLastWin32Error());
    }

    private static void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static bool ForegroundIsAllowedProgram()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId || IsOtzariaPid(pid)) return false;
            return Programs.ShouldYieldTo(pid);
        }
        catch { return false; }
    }

    private static bool IsOtzariaPid(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string path = OtzariaPath();
            string name = string.IsNullOrEmpty(path) ? "otzaria" : Path.GetFileNameWithoutExtension(path);
            return p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>חזרה לאוצריא מתוכנה מותרת (Ctrl+Alt+O)</summary>
    private static void ReturnToOtzaria()
    {
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            string path = OtzariaPath();
            string name = string.IsNullOrEmpty(path) ? "otzaria" : Path.GetFileNameWithoutExtension(path);
            var otz = Process.GetProcessesByName(name).FirstOrDefault(p => p.SessionId == session);
            if (otz != null && otz.MainWindowHandle != IntPtr.Zero) BringToFront(otz.MainWindowHandle);
        }
        catch (Exception ex) { Config.Log("return to otzaria: " + ex.Message); }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool alt = (k.flags & 0x20) != 0;
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            uint vk = k.vkCode;
            bool block =
                vk is 0x5B or 0x5C ||                     // Win
                (alt && vk is 0x09 or 0x1B or 0x73) ||   // Alt+Tab, Alt+Esc, Alt+F4
                (ctrl && vk == 0x1B);                     // Ctrl+Esc
            // Alt+F4 עובד בתוכנה מותרת שנמצאת בחזית, כדי שאפשר לסגור אותה ולחזור לאוצריא
            if (block && alt && vk == 0x73 && ForegroundIsAllowedProgram()) block = false;
            // Ctrl+Alt+O: חזרה לאוצריא מתוכנה מותרת (רק כשתוכנה מותרת בחזית; באוצריא עצמה המקשים עוברים הלאה)
            if (ctrl && alt && vk == 0x4F && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104) && ForegroundIsAllowedProgram())
            {
                Task.Run(ReturnToOtzaria);
                return (IntPtr)1;
            }
            // Ctrl+Alt+Shift+F12: חלון קוד שחזור לביטול חירום של הנעילה
            if (ctrl && alt && vk == 0x7B && (GetAsyncKeyState(0x10) & 0x8000) != 0 && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104))
            {
                Tray.Invoke(EmergencyForm.Open);
                return (IntPtr)1;
            }
            if (block) return (IntPtr)1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
