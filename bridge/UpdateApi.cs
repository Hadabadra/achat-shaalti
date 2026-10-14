using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// בדיקת עדכון והתקנת עדכון: משווה את גרסת הגשר לגרסה האחרונה בעמוד ההוצאות בגיטהאב,
/// ובבקשה מפורשת מוריד את OtzariaBridge.exe החדש, מאמת את ה-SHA256 מול SHA256SUMS.txt שבהוצאה,
/// ומריץ אותו עם --install --quiet (המתקין סוגר את הגשר הישן, מחליף את הקובץ ומפעיל מחדש).
/// </summary>
public static class UpdateApi
{
    public const string Repo = "e0548433917-gif/achat-shaalti";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";
    public const string AllReleasesUrl = "https://github.com/" + Repo + "/releases";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=20";

    private static readonly HttpClient Http = Create();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _checked = DateTime.MinValue;
    private static Info _last;

    public record Info(string Current, string Latest, string Tag, bool Newer, string Url, string ExeUrl, string SumsUrl, string Notes);

    /// <summary>
    /// תגית ה-Release בפורק האישי (RELEASE_VERSION.txt, למשל v0.0.5) היא מספור עצמאי של המשתמש,
    /// לא גרסת הגשר עצמה (Program.Version, למשל 3.5.0) - אי אפשר להשוות ביניהן ישירות. לכן ה-CI
    /// מעלה גם קובץ BRIDGE_VERSION.txt עם Program.Version בפועל, וכאן משווים מולו, לא מול התגית.
    /// </summary>
    private const string BridgeVersionAssetName = "BRIDGE_VERSION.txt";

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OtzariaBridge/" + Program.Version);
        return c;
    }

    public static void Register()
    {
        Server.Route("/sys/update-check", async b => Shape(await Check(b.Bool("force"))));
        Server.Route("/sys/update-install", async _ => await Install());
    }

    private static object Shape(Info i) => new
    {
        current = i.Current,
        latest = i.Latest,
        tag = i.Tag,
        newer = i.Newer,
        url = i.Url,
        releasesUrl = ReleasesUrl,
        canInstall = i.Newer && i.ExeUrl != null && !Kiosk.IsActive,
        notes = i.Notes,
    };

    public static Version Parse(string s)
    {
        var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    public static async Task<Info> Check(bool force)
    {
        await Gate.WaitAsync();
        try
        {
            // תוצאה אחרונה תקפה 2 דקות בלבד, ובבדיקה ידנית (force) תמיד פונים מחדש
            if (!force && _last != null && DateTime.UtcNow - _checked < TimeSpan.FromMinutes(2)) return _last;

            string json;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                using var res = await Http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                json = await res.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                throw new BridgeException("internal", "בדיקת העדכון נכשלה (אין חיבור לאינטרנט או ש-GitHub לא זמין): " + ex.Message, 502);
            }

            // מועמד יחיד: ה-Release האחרון (latest=true) בפורק האישי, לא "הכי גבוה לפי תגית" - כי
            // התגית כאן היא מספור עצמאי (RELEASE_VERSION.txt), לא גרסת הגשר, ולכן לא בר-השוואה כסדר.
            string bestTag = null, bestUrl = null, exeUrl = null, sumsUrl = null, bridgeVerUrl = null, notes = null;
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    if (tag == null) continue;
                    if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                    if (rel.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
                    bool isLatest = rel.TryGetProperty("latest", out var l) && l.GetBoolean();
                    if (!isLatest && bestTag != null) continue; // כבר נמצא Release מסומן "latest" - עדיף עליו
                    bestTag = tag;
                    bestUrl = rel.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                    notes = rel.TryGetProperty("body", out var bd) ? bd.GetString() : null;
                    exeUrl = sumsUrl = bridgeVerUrl = null;
                    if (rel.TryGetProperty("assets", out var assets))
                        foreach (var a in assets.EnumerateArray())
                        {
                            string name = a.GetProperty("name").GetString();
                            string dl = a.GetProperty("browser_download_url").GetString();
                            if (string.Equals(name, "OtzariaBridge.exe", StringComparison.OrdinalIgnoreCase)) exeUrl = dl;
                            if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = dl;
                            if (string.Equals(name, BridgeVersionAssetName, StringComparison.OrdinalIgnoreCase)) bridgeVerUrl = dl;
                        }
                    if (isLatest) break;
                }
            }

            Version latestBridgeVer = null;
            if (bridgeVerUrl != null)
            {
                try { latestBridgeVer = Parse((await Http.GetStringAsync(bridgeVerUrl)).Trim()); }
                catch { /* ההשוואה תיכשל בבטחה (לא "newer") אם זה נכשל */ }
            }
            // במאגר הראשי תגית ה-Release היא גרסת הגשר עצמה (v3.7.0) - אם אין BRIDGE_VERSION.txt משווים מול התגית
            if (latestBridgeVer == null && bestTag != null) latestBridgeVer = Parse(bestTag.TrimStart('v', 'V'));
            var cur = Parse(Program.Version);
            _last = new Info(Program.Version, latestBridgeVer?.ToString(3), bestTag, latestBridgeVer != null && cur != null && latestBridgeVer > cur,
                bestUrl ?? ReleasesUrl, exeUrl, sumsUrl, notes);
            _checked = DateTime.UtcNow;
            return _last;
        }
        finally { Gate.Release(); }
    }

    /// <summary>הורדה, אימות והפעלת המתקין של הגרסה החדשה. הגשר הנוכחי ייסגר על ידי המתקין</summary>
    public static async Task<object> Install()
    {
        if (Kiosk.IsActive) throw BridgeException.Locked("עדכון הגשר אינו זמין בזמן נעילה. בטלו את הנעילה, עדכנו, והפעילו אותה שוב.");
        var info = await Check(true);
        if (!info.Newer) return new { started = false, message = "הגשר מעודכן (" + info.Current + ")" };
        if (info.ExeUrl == null) throw BridgeException.NotFound("בהוצאה " + info.Tag + " אין קובץ OtzariaBridge.exe");

        string dir = Path.Combine(Path.GetTempPath(), "OtzariaBridge-update");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "OtzariaBridge.exe");
        byte[] data;
        try { data = await Http.GetByteArrayAsync(info.ExeUrl); }
        catch (Exception ex) { throw new BridgeException("internal", "ההורדה נכשלה: " + ex.Message, 502); }

        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        // אימות חובה (fail-closed): בלי SHA256SUMS תקין ותואם — לא מריצים דבר.
        // קודם היה "אם יש sums ותואם"; חוסר קובץ או regex שלא תפס → ריצה בלי אימות.
        if (info.SumsUrl == null)
            throw new BridgeException("failed", "בהוצאה " + info.Tag + " אין SHA256SUMS.txt לאימות. העדכון בוטל.", 500);
        string sums;
        try { sums = await Http.GetStringAsync(info.SumsUrl); }
        catch (Exception ex) { throw new BridgeException("internal", "הורדת קובץ האימות נכשלה: " + ex.Message, 502); }
        var m = Regex.Match(sums, @"([0-9a-fA-F]{64})\s+\*?OtzariaBridge\.exe", RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new BridgeException("failed", "קובץ האימות של ההוצאה אינו בפורמט צפוי. העדכון בוטל.", 500);
        if (!string.Equals(m.Groups[1].Value, hash, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("failed", "הקובץ שהורד אינו תואם לחתימת ה-SHA256 שבהוצאה. העדכון בוטל.", 500);

        await File.WriteAllBytesAsync(exe, data);

        // מניעת TOCTOU: תיקיית הזמנית ציבורית, אז מאמתים מחדש את הקובץ שעל הדיסק רגע לפני ההרצה —
        // כך אין חלון שבו תהליך מקומי מחליף את ה-exe בין הכתיבה להרצה.
        string onDisk = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(exe))).ToLowerInvariant();
        if (!string.Equals(onDisk, hash, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("failed", "הקובץ שנכתב לדיסק שונה מהמאומת. העדכון בוטל.", 500);
        Config.Log("update: " + info.Current + " -> " + info.Latest + " sha256=" + hash);

        // המתקין החדש סוגר את הגשר הזה, מעתיק את עצמו ומפעיל מחדש
        Process.Start(new ProcessStartInfo(exe, "--install --quiet") { UseShellExecute = false, WorkingDirectory = dir });
        return new { started = true, version = info.Latest, sha256 = hash };
    }
}
