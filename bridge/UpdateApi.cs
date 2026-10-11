using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// בדיקת עדכון: משווה את גרסת הגשר לגרסה האחרונה בעמוד ההוצאות בגיטהאב.
/// הגשר לא מוריד ולא מחליף את עצמו — הוא רק מדווח, וההורדה נעשית ידנית מעמוד ההוצאות.
/// </summary>
public static class UpdateApi
{
    public const string ReleasesUrl = "https://github.com/e0548433917-gif/otzaria-system-tools/releases/latest";
    private const string ApiUrl = "https://api.github.com/repos/e0548433917-gif/otzaria-system-tools/releases?per_page=15";

    private static readonly HttpClient Http = Create();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _checked = DateTime.MinValue;
    private static object _last;

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OtzariaBridge/" + Program.Version);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static void Register()
    {
        Server.Route("/sys/update-check", Check);
    }

    private static Version Parse(string s)
    {
        var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    private static async Task<object> Check(JsonElement b)
    {
        bool force = b.Bool("force");
        await Gate.WaitAsync();
        try
        {
            // תוצאה אחרונה תקפה 10 דקות, כדי לא להציף את GitHub
            if (!force && _last != null && DateTime.UtcNow - _checked < TimeSpan.FromMinutes(10)) return _last;

            string json;
            try { json = await Http.GetStringAsync(ApiUrl); }
            catch (Exception ex)
            {
                throw new BridgeException("internal", "בדיקת העדכון נכשלה (אין חיבור לאינטרנט או ש-GitHub לא זמין): " + ex.Message, 502);
            }

            Version best = null;
            string bestTag = null, bestUrl = null;
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    // רק הוצאות של הגשר (bridge-vX.Y.Z), ללא טיוטות
                    if (tag == null || !tag.StartsWith("bridge-v", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                    var v = Parse(tag);
                    if (v == null || (best != null && v <= best)) continue;
                    best = v;
                    bestTag = tag;
                    bestUrl = rel.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                }
            }

            var cur = Parse(Program.Version);
            _last = new
            {
                current = Program.Version,
                latest = best?.ToString(),
                tag = bestTag,
                newer = best != null && cur != null && best > cur,
                url = bestUrl ?? ReleasesUrl,
                releasesUrl = ReleasesUrl,
            };
            _checked = DateTime.UtcNow;
            return _last;
        }
        finally { Gate.Release(); }
    }
}
