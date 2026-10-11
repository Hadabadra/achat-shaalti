using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// תוכנות מותרות לפתיחה מתוך אוצריא גם במצב נעילה (כתבי הקודש, אוצר החכמה, בר אילן, Word ועוד).
/// הרשימה מוגדרת בסיסמה בלבד. הפתיחה היא לפי מזהה מהרשימה — אף פעם לא לפי נתיב חופשי.
/// בזמן שתוכנה מהרשימה בחזית, השומר של הנעילה לא מחזיר את אוצריא לחזית.
/// </summary>
public static class Programs
{
    private const string Key = "kiosk.programs";
    private const string AutoKey = "kiosk.autoTorahPrograms";
    private const string KeywordsKey = "kiosk.autoTorahKeywords";
    private const int MaxPrograms = 20;
    private const int GraceSeconds = 30;

    // מעטפות ומנהלי מערכת שאסור להוסיף לרשימה (עוקף את הנעילה)
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "regedit",
        "taskmgr", "mmc", "control", "conhost", "wt", "rundll32", "msiexec"
    };

    private static DateTime _graceUntil = DateTime.MinValue;
    private static DateTime _lastLaunchAt = DateTime.MinValue;

    public sealed class Entry
    {
        public string Id = "";
        public string Name = "";
        public string Path = "";
        public string[] Procs = Array.Empty<string>();
        public bool Auto;
    }

    // מילות מפתח לזיהוי תוכנות תורניות בשמות התוכנות המותקנות (אפשר להרחיב ב-kiosk.autoTorahKeywords)
    private static readonly string[] TorahKeywords =
    {
        "אוצר החכמה", "אוצר הספרים", "בר אילן", "בר-אילן", "כתבי הקודש", "ספריא",
        "otzar hachochma", "otzar hasfarim", "bar-ilan", "bar ilan", "responsa", "kitvei hakodesh",
    };

    private static readonly object AutoGate = new();
    private static List<Entry> _autoCache = new();
    private static DateTime _autoAt = DateTime.MinValue;

    public static bool AutoEnabled => Config.Get(AutoKey, false);

    public static void Register()
    {
        Server.Route("/kiosk/programs", _ => ListResult());
        Server.Route("/kiosk/programs/launch", Launch);
        Server.Route("/kiosk/programs/add", Add);
        Server.Route("/kiosk/programs/remove", Remove);
        Server.Route("/kiosk/programs/detect", _ => Detect());
        Server.Route("/kiosk/programs/auto", SetAuto);
    }

    // ---------- אחסון ----------

    /// <summary>הרשימה המלאה: ידנית + (אם הזיהוי האוטומטי דולק) תוכנות תורניות שזוהו</summary>
    public static List<Entry> Load()
    {
        var list = LoadManual();
        if (AutoEnabled)
            foreach (var a in DetectAuto())
                if (!list.Any(x => x.Path.Equals(a.Path, StringComparison.OrdinalIgnoreCase))) list.Add(a);
        return list;
    }

    private static List<Entry> LoadManual()
    {
        var list = new List<Entry>();
        foreach (var s in Config.GetList(Key))
        {
            try
            {
                if (JsonNode.Parse(s) is not JsonObject o) continue;
                string id = (string)o["id"], name = (string)o["name"], path = (string)o["path"];
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) continue;
                string[] procs = o["procs"] is JsonArray a
                    ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                list.Add(new Entry { Id = id, Name = name ?? id, Path = path, Procs = procs });
            }
            catch { }
        }
        return list;
    }

    private static void Save(List<Entry> list)
    {
        var arr = new JsonArray(list.Select(e => (JsonNode)new JsonObject
        {
            ["id"] = e.Id,
            ["name"] = e.Name,
            ["path"] = e.Path,
            ["procs"] = new JsonArray(e.Procs.Select(p => (JsonNode)p).ToArray()),
        }).ToArray());
        Config.Set(Key, arr);
        Config.Save();
    }

    private static string[] ProcNames(Entry e) =>
        e.Procs.Append(System.IO.Path.GetFileNameWithoutExtension(e.Path))
            .Select(x => x.ToLowerInvariant()).Distinct().ToArray();

    // ---------- נקודות קצה ----------

    private static object ListResult() => new
    {
        autoTorah = AutoEnabled,
        programs = Load().Select(e => new
        {
            id = e.Id,
            name = e.Name,
            path = e.Path,
            procs = e.Procs,
            exists = File.Exists(e.Path),
            auto = e.Auto,
        }).ToArray()
    };

    private static object SetAuto(JsonElement b)
    {
        Kiosk.RequirePassword(b.Str("password"));
        Config.Set(AutoKey, JsonValue.Create(b.Bool("enabled")));
        Config.Save();
        lock (AutoGate) _autoAt = DateTime.MinValue; // סריקה מחדש
        return ListResult();
    }

    private static object Launch(JsonElement b)
    {
        string id = b.Req("id");
        var e = Load().FirstOrDefault(x => x.Id == id)
                ?? throw BridgeException.NotFound("התוכנה אינה ברשימת התוכנות המותרות");
        if (!File.Exists(e.Path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + e.Path);

        _graceUntil = DateTime.UtcNow.AddSeconds(GraceSeconds);
        _lastLaunchAt = DateTime.UtcNow;
        using (var started = Process.Start(new ProcessStartInfo(e.Path)
        {
            UseShellExecute = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(e.Path),
        }))
        {
            if (started != null) RememberLaunched(started.Id);
        }
        Task.Run(() => Focus(e));
        return new { opened = true };
    }

    private static object Add(JsonElement b)
    {
        Kiosk.RequirePassword(b.Str("password"));
        string name = (b.Str("name") ?? "").Trim();
        string path = (b.Str("path") ?? "").Trim().Trim('"');
        if (name.Length == 0 || name.Length > 60) throw BridgeException.BadRequest("יש להזין שם באורך של עד 60 תווים");
        if (!System.IO.Path.IsPathRooted(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw BridgeException.BadRequest("יש להזין נתיב מלא לקובץ הפעלה (exe)");
        if (!File.Exists(path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + path);
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (Blocked.Contains(stem)) throw BridgeException.Forbidden("אי אפשר להוסיף את התוכנה הזו לרשימה");

        var procs = new List<string>();
        foreach (string p in (b.Str("procs") ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(p.Trim());
            if (!Regex.IsMatch(n, @"^[\w .\-]{1,60}$")) throw BridgeException.BadRequest("שם תהליך לא תקין: " + p);
            if (Blocked.Contains(n)) throw BridgeException.Forbidden("אי אפשר להוסיף את התהליך " + n);
            procs.Add(n);
        }

        var list = LoadManual();
        if (list.Count >= MaxPrograms) throw BridgeException.BadRequest("אפשר להגדיר עד " + MaxPrograms + " תוכנות");
        if (Load().Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw BridgeException.Exists("התוכנה כבר ברשימה");

        list.Add(new Entry
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            Name = name,
            Path = path,
            Procs = procs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        });
        Save(list);
        return ListResult();
    }

    private static object Remove(JsonElement b)
    {
        Kiosk.RequirePassword(b.Str("password"));
        string id = b.Req("id");
        if (id.StartsWith("auto-", StringComparison.Ordinal))
            throw BridgeException.BadRequest("תוכנה שזוהתה אוטומטית אי אפשר להסיר. כבו את הזיהוי האוטומטי");
        var list = LoadManual();
        if (list.RemoveAll(x => x.Id == id) == 0) throw BridgeException.NotFound("התוכנה אינה ברשימה");
        Save(list);
        return ListResult();
    }

    /// <summary>מאתר תוכנות נפוצות להצעה (כרגע: Word), לפי App Paths ברישום</summary>
    private static object Detect()
    {
        var found = new List<object>();
        string word = AppPath("WINWORD.EXE");
        if (word != null) found.Add(new { name = "Word", path = word, procs = "WINWORD" });
        return new { found = found.ToArray() };
    }

    private static string AppPath(string exe)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var bk = RegistryKey.OpenBaseKey(hive, view);
                    using var k = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                    string v = (k?.GetValue(null) as string)?.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                }
                catch { }
            }
        return null;
    }

    // ---------- זיהוי אוטומטי של תוכנות תורניות ----------

    private static List<Entry> DetectAuto()
    {
        lock (AutoGate)
        {
            if ((DateTime.UtcNow - _autoAt).TotalSeconds < 60) return _autoCache;
            var found = new List<Entry>();
            try
            {
                string word = AppPath("WINWORD.EXE");
                if (word != null) AddAuto(found, "Word", word, new[] { "WINWORD" });

                var keywords = TorahKeywords.Concat(Config.GetList(KeywordsKey)).Select(k => k.ToLowerInvariant()).ToArray();
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        try
                        {
                            using var bk = RegistryKey.OpenBaseKey(hive, view);
                            using var root = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                            if (root == null) continue;
                            foreach (string sub in root.GetSubKeyNames())
                            {
                                using var k = root.OpenSubKey(sub);
                                string display = (k?.GetValue("DisplayName") as string)?.Trim();
                                if (string.IsNullOrEmpty(display)) continue;
                                string low = display.ToLowerInvariant();
                                if (!keywords.Any(low.Contains)) continue;
                                string exe = ResolveExe(k);
                                if (exe != null) AddAuto(found, display, exe, Array.Empty<string>());
                            }
                        }
                        catch { }
                    }
            }
            catch (Exception ex) { Config.Log("programs auto-detect: " + ex.Message); }
            _autoCache = found;
            _autoAt = DateTime.UtcNow;
            return _autoCache;
        }
    }

    private static void AddAuto(List<Entry> list, string name, string path, string[] procs)
    {
        if (Blocked.Contains(System.IO.Path.GetFileNameWithoutExtension(path))) return;
        if (list.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Substring(0, 8).ToLowerInvariant();
        list.Add(new Entry { Id = "auto-" + hash, Name = name, Path = path, Procs = procs, Auto = true });
    }

    /// <summary>קובץ ההפעלה של תוכנה מותקנת: לפי DisplayIcon, ואם אין — קובץ exe הגדול בתיקיית ההתקנה</summary>
    private static string ResolveExe(RegistryKey k)
    {
        string icon = k.GetValue("DisplayIcon") as string;
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var m = Regex.Match(icon, "^\\s*\"?([^\",]+\\.exe)", RegexOptions.IgnoreCase);
            if (m.Success && File.Exists(m.Groups[1].Value) && !IsHelperExe(m.Groups[1].Value)) return m.Groups[1].Value;
        }
        string loc = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
        {
            try
            {
                return Directory.EnumerateFiles(loc, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !IsHelperExe(f)).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            }
            catch { }
        }
        return null;
    }

    private static bool IsHelperExe(string path)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return n.StartsWith("unins") || n.StartsWith("uninstall") || n.StartsWith("setup") || n.StartsWith("update") || n.StartsWith("vc_redist");
    }

    // ---------- שילוב עם השומר של הנעילה ----------

    // תהליכים שנפתחו דרך הגשר (מזהה → זמן פתיחה). נשמרים 10 דקות, כדי לזהות גם תהליכי בן
    // של מפעיל שכבר נסגר (למשל תוכנה שמפעילה תהליך אחר שמציג את החלון).
    private static readonly Dictionary<int, DateTime> Launched = new();
    private static readonly Dictionary<int, (int parent, DateTime at)> ParentCache = new();
    private static readonly object PidGate = new();

    private static void RememberLaunched(int pid)
    {
        lock (PidGate)
        {
            foreach (int k in Launched.Where(x => DateTime.UtcNow - x.Value > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToList())
                Launched.Remove(k);
            Launched[pid] = DateTime.UtcNow;
        }
    }

    private static int ParentOf(int pid)
    {
        lock (PidGate)
            if (ParentCache.TryGetValue(pid, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(10)) return c.parent;
        int parent = 0;
        try
        {
            using var q = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid);
            foreach (ManagementObject mo in q.Get())
                using (mo) parent = Convert.ToInt32(mo["ParentProcessId"]);
        }
        catch { /* אין גישה ל-WMI */ }
        lock (PidGate) ParentCache[pid] = (parent, DateTime.UtcNow);
        return parent;
    }

    /// <summary>האם התהליך נפתח דרך הגשר, או שהוא תהליך בן (עד 6 דורות) של תהליך שנפתח כך</summary>
    private static bool IsLaunchedTree(int pid)
    {
        for (int i = 0; i < 6 && pid > 4; i++)
        {
            lock (PidGate) if (Launched.ContainsKey(pid)) return true;
            pid = ParentOf(pid);
        }
        return false;
    }

    /// <summary>תיקיות רחבות מדי כדי להתיר את כל מה שבהן (Windows, Program Files, תיקיית משתמש...)</summary>
    private static bool BroadDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return true;
        string d = dir.TrimEnd('\\');
        string root = (System.IO.Path.GetPathRoot(dir) ?? "").TrimEnd('\\');
        if (d.Length <= root.Length) return true;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (d.Equals(win, StringComparison.OrdinalIgnoreCase) || d.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var f in new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        })
        {
            string x = Environment.GetFolderPath(f).TrimEnd('\\');
            if (x.Length > 0 && d.Equals(x, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// האם השומר צריך לוותר על החזרת אוצריא לחזית. כן אם: פתיחה טרייה (30 שניות), או שהתהליך בחזית הוא
    /// תוכנה מהרשימה — לפי שם תהליך, לפי קובץ ההפעלה המלא, לפי תיקיית ההתקנה (תהליכי עזר ומפעילים נפרדים),
    /// או לפי עץ התהליכים של מה שנפתח דרך הגשר.
    /// </summary>
    public static bool ShouldYieldTo(uint foregroundPid)
    {
        if (DateTime.UtcNow < _graceUntil) return true;
        try
        {
            using var p = Process.GetProcessById((int)foregroundPid);
            if (MatchesList(p, Load())) return true;
            if (IsLaunchedTree((int)foregroundPid)) return true;
            // תוכנה שהחלון שלה שייך לתהליך אחר מהקובץ שהופעל (מפעיל נפרד, תהליך עזר, מופע יחיד):
            // כל תהליך חדש שנפתח בדקות שאחרי הפתיחה דרך הגשר, שאינו מעטפת/מנהל מערכת/אוצריא/הגשר
            return !IsOwnOrBlocked(p) && IsNewAfterLaunch(p);
        }
        catch { return false; }
    }

    /// <summary>האם התהליך הוא תוכנה מהרשימה: לפי שם תהליך, קובץ הפעלה מלא, או תיקיית ההתקנה (לא רחבה)</summary>
    private static bool MatchesList(Process p, IEnumerable<Entry> list)
    {
        string fg = p.ProcessName.ToLowerInvariant();
        if (list.Any(e => ProcNames(e).Contains(fg))) return true;
        string exe = null;
        try { exe = p.MainModule?.FileName; } catch { /* תהליך מוגבה או ללא גישה */ }
        if (string.IsNullOrEmpty(exe)) return false;
        foreach (var e in list)
        {
            if (exe.Equals(e.Path, StringComparison.OrdinalIgnoreCase)) return true;
            string dir = System.IO.Path.GetDirectoryName(e.Path);
            if (!BroadDir(dir) && exe.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>אוצריא, הגשר, מעטפות ומנהלי מערכת: לעולם לא "תוכנה שנפתחה"</summary>
    private static bool IsOwnOrBlocked(Process p)
    {
        if (p.Id == Environment.ProcessId) return true;
        string n = p.ProcessName;
        if (Blocked.Contains(n)) return true;
        string otz = System.IO.Path.GetFileNameWithoutExtension(Kiosk.OtzariaPath() ?? "otzaria");
        return n.Equals(otz, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartedAfterLaunch(Process p)
    {
        if (_lastLaunchAt == DateTime.MinValue) return false;
        try { return p.StartTime.ToUniversalTime() >= _lastLaunchAt.AddSeconds(-3); }
        catch { return false; } // תהליך מוגבה: אין גישה לזמן ההתחלה
    }

    /// <summary>תהליך שהתחיל בין 3 שניות לפני הפתיחה דרך הגשר לבין 90 שניות אחריה</summary>
    private static bool IsNewAfterLaunch(Process p)
    {
        if (!StartedAfterLaunch(p)) return false;
        try { return p.StartTime.ToUniversalTime() <= _lastLaunchAt.AddSeconds(90); }
        catch { return false; }
    }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>
    /// מחפש חלון ראשי גלוי (עם כותרת, בלי בעלים) של תהליך שהוא התוכנה שנפתחה: מהרשימה, או חדש אחרי הפתיחה
    /// (כולל תהליכי בן). כך מוצאים את החלון גם כשהוא לא שייך לתהליך עם שם הקובץ שהופעל.
    /// </summary>
    private static IntPtr FindLaunchedWindow(int session, Entry e)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.SessionId != session || IsOwnOrBlocked(p)) return true;
                if (MatchesList(p, new[] { e }) || (StartedAfterLaunch(p) && (IsNewAfterLaunch(p) || IsLaunchedTree((int)pid))))
                {
                    found = h;
                    return false;
                }
            }
            catch { /* תהליך נסגר או ללא גישה */ }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>מביא את חלון התוכנה שנפתחה לחזית (עד כ-20 שניות; תוכנות כבדות נפתחות לאט)</summary>
    private static void Focus(Entry e)
    {
        var names = ProcNames(e);
        int session = Process.GetCurrentProcess().SessionId;
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(500);
            try
            {
                foreach (string name in names)
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        using (p)
                        {
                            if (p.SessionId != session) continue;
                            IntPtr h = p.MainWindowHandle;
                            if (h == IntPtr.Zero) continue;
                            Kiosk.BringToFront(h);
                            return;
                        }
                    }
                // החלון שייך לתהליך אחר מהקובץ שהופעל (מפעיל נפרד): מחפשים לפי החלונות הגלויים
                IntPtr w = FindLaunchedWindow(session, e);
                if (w != IntPtr.Zero)
                {
                    Kiosk.BringToFront(w);
                    Thread.Sleep(600);
                    Kiosk.BringToFront(w);
                    Config.Log("programs focus: החלון נמצא לפי תהליך חדש ולא לפי שם הקובץ (" + e.Name + ")");
                    return;
                }
            }
            catch (Exception ex) { Config.Log("programs focus: " + ex.Message); }
        }
    }
}
