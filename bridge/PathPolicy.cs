using System.Runtime.InteropServices;
using System.Text;

namespace OtzariaBridge;

/// <summary>
/// לוגיקה טהורה (בלי תלות ב-Config או בממשק) לנרמול נתיבים לצורתם הקנונית ולבדיקת הכלה בתיקיות מותרות.
/// משמשת את מדיניות סייר הקבצים (FxPolicy) ואת EnsureNotProtected. כשל בפענוח = כשל סגור.
/// </summary>
public static class PathPolicy
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// נתיב מלא מנורמל (בלי .., בלי מפרידים מיותרים). זורק ArgumentException על נתיב התקן (\\?\, \\.\),
    /// זרם נתונים חלופי (file:stream), שם התקן שמור (CON, NUL...) או נתיב שאינו מלא.
    /// </summary>
    public static string Normalize(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) throw new ArgumentException("נתיב ריק");
        p = p.Trim();
        if (p.IndexOf('\0') >= 0) throw new ArgumentException("נתיב לא חוקי");
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.StartsWith(@"\??\") || p.StartsWith("//?/") || p.StartsWith("//./"))
            throw new ArgumentException("נתיב מסוג התקן אינו נתמך");
        if (!Path.IsPathFullyQualified(p)) throw new ArgumentException("נדרש נתיב מלא");
        string full = Path.GetFullPath(p);
        if (full.StartsWith(@"\\?\") || full.StartsWith(@"\\.\")) throw new ArgumentException("נתיב מסוג התקן אינו נתמך");
        if (full.IndexOf(':', 2) >= 0) throw new ArgumentException("זרם נתונים חלופי (ADS) אינו נתמך");
        string root = Path.GetPathRoot(full) ?? "";
        foreach (string part in full.Substring(root.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            string stem = part.TrimEnd(' ', '.');
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot).TrimEnd(' ');
            if (Reserved.Contains(stem)) throw new ArgumentException("שם התקן שמור אינו נתמך: " + part);
        }
        return full;
    }

    /// <summary>
    /// הנתיב הסופי כפי שמערכת הקבצים מחזירה: שמות ארוכים (8.3), צמתים (junction) וקישורים סימבוליים מפוענחים.
    /// לנתיב שאינו קיים מפענחים את ההורה הקיים הקרוב ומצרפים את השאר.
    /// strict: כשל בפענוח (הרשאה, קישור שבור, נתיב ארוך) זורק IOException; אחרת מחזיר את הנתיב כפי שהוא.
    /// </summary>
    public static string Canonical(string fullPath, bool strict)
    {
        string full = Path.GetFullPath(fullPath);
        string probe = full, tail = "";
        // Attributes מחזיר -1 לנתיב שאינו קיים; קישור שבור (dangling) נחשב כקיים, ולכן הפענוח שלו ייכשל סגור
        while (!string.IsNullOrEmpty(probe) && (int)new FileInfo(probe).Attributes == -1)
        {
            tail = Path.Combine(Path.GetFileName(probe), tail);
            probe = Path.GetDirectoryName(probe);
        }
        if (string.IsNullOrEmpty(probe))
        {
            if (strict) throw new IOException("לא נמצא הורה קיים לנתיב");
            return full;
        }
        try
        {
            using var h = CreateFileW(probe, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); // FILE_FLAG_BACKUP_SEMANTICS
            if (h.IsInvalid) { if (strict) throw new IOException("פענוח הנתיב נכשל (קוד " + Marshal.GetLastWin32Error() + ")"); return full; }
            var sb = new StringBuilder(32768);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) { if (strict) throw new IOException("פענוח הנתיב הסופי נכשל"); return full; }
            string r = sb.ToString();
            if (r.StartsWith(@"\\?\UNC\")) r = @"\\" + r.Substring(8);
            else if (r.StartsWith(@"\\?\")) r = r.Substring(4);
            return tail.Length == 0 ? r : Path.Combine(r, tail.TrimEnd('\\'));
        }
        catch (IOException) { throw; }
        catch { if (strict) throw new IOException("פענוח הנתיב נכשל"); return full; }
    }

    /// <summary>האם path הוא root עצמו או נמצא מתחתיו. השוואה לא רגישה לרישיות, בטוחה לתחיליות אחים (C:\a מול C:\ab)</summary>
    public static bool IsUnder(string path, string root)
    {
        string r = root.TrimEnd('\\');
        string p = path.TrimEnd('\\');
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>נתיב מנורמל וקנוני (כשל סגור: זורק ArgumentException/IOException)</summary>
    public static string Resolve(string path) => Canonical(Normalize(path), strict: true);

    /// <summary>
    /// האם הנתיב (אחרי נרמול ופענוח צמתים) נמצא באחת התיקיות המותרות. canonRoots חייבים להיות קנוניים (Resolve).
    /// כל חריגה = false.
    /// </summary>
    public static bool IsAllowed(string path, IEnumerable<string> canonRoots)
    {
        try
        {
            string canon = Resolve(path);
            foreach (string r in canonRoots)
                if (IsUnder(canon, r)) return true;
            return false;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringBuilder buf, uint len, uint flags);
}
