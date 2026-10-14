// E2 / #30 ממצא 8: עדכון השירות בלי להריץ מורם את העותק שב-%LOCALAPPDATA% (תיקייה שהמשתמש יכול לכתוב אליה).
// השירות (SYSTEM, רץ מ-Program Files) מוריד בעצמו את OtzariaBridge.exe מההוצאה הרשמית ב-GitHub,
// מאמת SHA256 מול SHA256SUMS.txt של אותה הוצאה, מחליף את עצמו ומתאתחל. רק שדרוג (גרסה גבוהה יותר), רק
// כשהנעילה לא נאכפת. אם ההוצאה לא קיימת (גרסת פיתוח) — הלקוח חוזר לדרך הישנה (UAC).
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

public static class SvcUpdate
{
    static JsonObject Err(string m) => new() { ["ok"] = false, ["code"] = "failed", ["message"] = m };

    /// <summary>רץ בתוך השירות. enforced = האם הנעילה נאכפת כרגע</summary>
    public static JsonObject Run(string version, bool enforced)
    {
        try
        {
            if (enforced) return Err("נעילה פעילה — עדכון עם סיסמה בלבד");
            var want = UpdateApi.Parse(version ?? ""); var cur = UpdateApi.Parse(Program.Version);
            if (want == null || cur == null || want <= cur) return Err("אין גרסה חדשה יותר");
            string tag = "v" + want.ToString(3);
            string baseUrl = "https://github.com/" + UpdateApi.Repo + "/releases/download/" + tag + "/";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OtzariaBridge-Service");
            string sums = http.GetStringAsync(baseUrl + "SHA256SUMS.txt").Result;
            var m = Regex.Match(sums, @"([0-9a-fA-F]{64})\s+\*?OtzariaBridge\.exe", RegexOptions.IgnoreCase);
            if (!m.Success) return Err("אין SHA256 בהוצאה " + tag);
            byte[] data = http.GetByteArrayAsync(baseUrl + "OtzariaBridge.exe").Result;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                return Err("הקובץ שהורד אינו תואם ל-SHA256");

            string exe = HardLock.ServiceExe, neu = exe + ".new", old = exe + ".old";
            File.WriteAllBytes(neu, data); // Program Files — רק SYSTEM/מנהלים כותבים
            try { File.Delete(old); } catch { }
            File.Move(exe, old);             // מותר להזיז exe רץ
            File.Move(neu, exe);
            HardLock.SvcLog("self-update: " + Program.Version + " -> " + tag + " (מאומת SHA256)");
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 >nul & sc stop {HardLock.ServiceName} & timeout /t 3 >nul & sc start {HardLock.ServiceName}")
            { UseShellExecute = false, CreateNoWindow = true });
            return new JsonObject { ["ok"] = true, ["version"] = want.ToString(3) };
        }
        catch (Exception ex) { HardLock.SvcLog("self-update: " + ex.Message); return Err(ex.Message); }
    }
}
