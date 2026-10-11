using System.Diagnostics;
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
    private const int MaxPrograms = 20;
    private const int GraceSeconds = 15;

    // מעטפות ומנהלי מערכת שאסור להוסיף לרשימה (עוקף את הנעילה)
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "regedit",
        "taskmgr", "mmc", "control", "conhost", "wt", "rundll32", "msiexec"
    };

    private static DateTime _graceUntil = DateTime.MinValue;

    public sealed class Entry
    {
        public string Id = "";
        public string Name = "";
        public string Path = "";
        public string[] Procs = Array.Empty<string>();
    }

    public static void Register()
    {
        Server.Route("/kiosk/programs", _ => ListResult());
        Server.Route("/kiosk/programs/launch", Launch);
        Server.Route("/kiosk/programs/add", Add);
        Server.Route("/kiosk/programs/remove", Remove);
        Server.Route("/kiosk/programs/detect", _ => Detect());
    }

    // ---------- אחסון ----------

    public static List<Entry> Load()
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
        programs = Load().Select(e => new
        {
            id = e.Id,
            name = e.Name,
            path = e.Path,
            procs = e.Procs,
            exists = File.Exists(e.Path),
        }).ToArray()
    };

    private static object Launch(JsonElement b)
    {
        string id = b.Req("id");
        var e = Load().FirstOrDefault(x => x.Id == id)
                ?? throw BridgeException.NotFound("התוכנה אינה ברשימת התוכנות המותרות");
        if (!File.Exists(e.Path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + e.Path);

        _graceUntil = DateTime.UtcNow.AddSeconds(GraceSeconds);
        Process.Start(new ProcessStartInfo(e.Path)
        {
            UseShellExecute = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(e.Path),
        });
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

        var list = Load();
        if (list.Count >= MaxPrograms) throw BridgeException.BadRequest("אפשר להגדיר עד " + MaxPrograms + " תוכנות");
        if (list.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
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
        var list = Load();
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

    // ---------- שילוב עם השומר של הנעילה ----------

    /// <summary>האם השומר צריך לוותר על החזרת אוצריא לחזית (תוכנה מותרת בחזית, או פתיחה טרייה)</summary>
    public static bool ShouldYieldTo(uint foregroundPid)
    {
        if (DateTime.UtcNow < _graceUntil) return true;
        try
        {
            string fg;
            using (var p = Process.GetProcessById((int)foregroundPid)) fg = p.ProcessName.ToLowerInvariant();
            return Load().Any(e => ProcNames(e).Contains(fg));
        }
        catch { return false; }
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
            }
            catch (Exception ex) { Config.Log("programs focus: " + ex.Message); }
        }
    }
}
