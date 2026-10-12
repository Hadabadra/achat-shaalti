using System.Text.Json;

namespace OtzariaBridge;

/// <summary>
/// זמני היום לשומר המסך.
/// המידע ההלכתי עצמו (זמני היום, תאריך עברי, שבת/חג) מגיע מלוח השנה המובנה של אוצריא עצמה
/// (ה-API ‏calendar.* בתוסף "הגדרות מערכת", החל מגרסת אוצריא 0.9.97) ולא מחושב כאן מחדש.
/// תוסף "הגדרות מערכת" (js/app.js) שולח POST ‏/kiosk/zmanim-cache מדי דקה עם מה שקיבל מאוצריא;
/// הגשר רק שומר את זה בזיכרון (לא בקובץ ההגדרות — זה מידע זמני שמתעדכן כל דקה) ומציג אותו.
/// אם הנתונים חסרים או ישנים מדי (מעל שעתיים — כנראה אוצריא סגורה או התוסף הושעה), מוצג "גיבוי"
/// המחושב כאן מקומית, בלי שבת/חג/עומר/יום-השנה (אלה דורשים לוח עברי אמיתי שאיננו רוצים לשכפל כאן).
///
/// לגיבוי יש שני מנועי חישוב אפשריים, לבחירת המנהל (kiosk.zmanimFallbackEngine, גם ב-js/sections/kiosk.js
/// בתוסף "הגדרות מערכת"): "noaa" (ברירת מחדל, ללא שינוי התנהגות למתקינים קיימים) — קירוב אסטרונומי
/// פשוט, הנץ/שקיעה בגובה פני הים בלבד; ו-"kosherjava" (חדש) — זמנים הלכתיים מלאים (גר"א/מג"א) כולל
/// תיקון גובה (issue #33, "תיקון גובה לזמנים"), אבל קוד צד-שלישי שעלול להיבדל מעט בזמנים גבוליים.
///
/// קרדיט: מנוע ה-kosherjava משתמש בחבילת ה-NuGet ‏"Zmanim" גרסה 1.5.0 — הפורט הרשמי ל-.NET/.NET Standard
/// של פרויקט KosherJava מאת Eliyahu Hershfeld (http://www.kosherjava.com/zmanim-project/),
/// ברישיון LGPL-2.1. זהו PackageReference רגיל (קישור דינמי), לא קוד מועתק/משוכתב לתוך הגשר —
/// ולכן אינו הופך את הקוד שלנו ל-LGPL, אך הקרדיט מוצג גם בחלון "אודות" וב-README.md כנדרש ברישיון.
/// </summary>
internal static class Zmanim
{
    private static readonly object Gate = new();
    private static string _cacheJson;
    private static DateTime _cacheUtc = DateTime.MinValue;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    /// <summary>חודשים עבריים תקינים לשדה "יום השנה" בהגדרות (כולל אדר א/ב לבחירה מפורשת)</summary>
    internal static readonly string[] YahrzeitMonths =
        { "תשרי", "חשון", "כסלו", "טבת", "שבט", "אדר", "אדר א", "אדר ב", "ניסן", "אייר", "סיון", "תמוז", "אב", "אלול" };

    public static void Register() => Server.Route("/kiosk/zmanim-cache", CacheSet);

    /// <summary>תוסף "הגדרות מערכת" דוחף לכאן את מה שקיבל מ-calendar.getDailyTimes/getJewishDate באוצריא</summary>
    private static object CacheSet(JsonElement b)
    {
        lock (Gate) { _cacheJson = b.GetRawText(); _cacheUtc = DateTime.UtcNow; }
        return new { ok = true };
    }

    // ---------- מיקום מותאם אישית (קואורדינטות): נשאר כאפשרות מתקדמת גם כשנבחרה עיר מרשימה ----------

    public static (double lat, double lon)? ParseLocation(string s)
    {
        var parts = (s ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out double lat) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out double lon)) return null;
        if (lat < -66 || lat > 66 || lon < -180 || lon > 180) return null; // מעבר לחוג הקוטב אין הנץ/שקיעה בכל יום
        return (lat, lon);
    }

    /// <summary>שורות לשומר המסך: זמני היום, כניסת שבת/חג, ספירת העומר ויום השנה — או מערך ריק</summary>
    public static string[] Sections(DateTime now)
    {
        var list = new List<string>();
        var cached = TryGetCache();
        if (cached != null)
        {
            var (dailyTimes, jdToday, jdTomorrow) = cached.Value;
            string zm = ZmanimLine(dailyTimes);
            if (zm != null) list.Add(zm);
            string ch = CandleHavdalaLine(now, dailyTimes, jdToday, jdTomorrow);
            if (ch != null) list.Add(ch);
            string omer = OmerLine(jdToday);
            if (omer != null) list.Add(omer);
            string yz = YahrzeitLine(jdToday);
            if (yz != null) list.Add(yz);
        }
        else
        {
            string fb = FallbackLine(now);
            if (fb != null) list.Add(fb);
        }
        return list.ToArray();
    }

    private static (JsonElement dailyTimes, JsonElement jewishDate, JsonElement jewishDateTomorrow)? TryGetCache()
    {
        string json; DateTime at;
        lock (Gate) { json = _cacheJson; at = _cacheUtc; }
        if (json == null || DateTime.UtcNow - at > MaxAge) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone(); // עותק עצמאי שנשאר תקף אחרי Dispose של ה-JsonDocument
            var dt = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("dailyTimes", out var a) ? a : default;
            var jd = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jewishDate", out var b) ? b : default;
            var jt = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jewishDateTomorrow", out var c) ? c : default;
            return (dt, jd, jt);
        }
        catch { return null; }
    }

    // ---------- זמני היום: מפתחות דינמיים (לא תקן קבוע באוצריא) לשמות מוכרים ----------

    private static readonly (string id, string[] keys, string he, string en)[] KnownZmanim =
    {
        ("alot", new[] { "alotHaShachar", "alotHashachar", "alot", "dawn" }, "עלות השחר", "Dawn"),
        ("netz", new[] { "sunrise", "netz", "hanetz", "netzHachama" }, "הנץ החמה", "Sunrise"),
        ("shemaMga", new[] { "sofZmanShmaMGA", "sofZmanShemaMGA", "sofZmanKriatShemaMGA", "sofZmanShmaMga" }, "סוף ק\"ש (מג\"א)", "Shema (MGA)"),
        ("shemaGra", new[] { "sofZmanShmaGRA", "sofZmanShemaGRA", "sofZmanKriatShemaGRA", "sofZmanShmaGra" }, "סוף ק\"ש (גר\"א)", "Shema (GRA)"),
        ("tefilaMga", new[] { "sofZmanTfilaMGA", "sofZmanTefilaMGA", "sofZmanTfilaMga" }, "סוף תפילה (מג\"א)", "Tefila (MGA)"),
        ("tefilaGra", new[] { "sofZmanTfilaGRA", "sofZmanTefilaGRA", "sofZmanTfilaGra" }, "סוף תפילה (גר\"א)", "Tefila (GRA)"),
        ("chatzot", new[] { "chatzot", "midday", "chatzos" }, "חצות", "Midday"),
        ("minchaGedola", new[] { "minchaGedola" }, "מנחה גדולה", "Mincha Gedola"),
        ("minchaKetana", new[] { "minchaKetana" }, "מנחה קטנה", "Mincha Ketana"),
        ("plag", new[] { "plagHamincha", "plagHaMincha" }, "פלג המנחה", "Plag HaMincha"),
        ("shkia", new[] { "sunset", "shkia", "shkiah" }, "שקיעה", "Sunset"),
        ("tzeit", new[] { "tzeit", "tzet", "tzeithakochavim", "nightfall" }, "צאת הכוכבים", "Nightfall"),
    };

    private static readonly string[] DefaultShow = { "alot", "netz", "shemaGra", "chatzot", "shkia", "tzeit" };

    private static bool TryPick(JsonElement obj, out string value, params string[] keys)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in obj.EnumerateObject())
            foreach (var k in keys)
                if (string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String) { value = prop.Value.GetString(); return true; }
                    if (prop.Value.ValueKind == JsonValueKind.Number) { value = prop.Value.ToString(); return true; }
                }
        return false;
    }

    private static string ZmanimLine(JsonElement dailyTimes)
    {
        if (dailyTimes.ValueKind != JsonValueKind.Object) return null;
        var show = Config.HasList("kiosk.zmanimShow") ? Config.GetList("kiosk.zmanimShow") : DefaultShow;
        var parts = new List<string>();
        foreach (var z in KnownZmanim)
        {
            if (!show.Contains(z.id, StringComparer.OrdinalIgnoreCase)) continue;
            if (!TryPick(dailyTimes, out string val, z.keys)) continue;
            parts.Add(Lang.T(z.he + " ", z.en + " ") + val);
        }
        if (parts.Count == 0) return null;
        int half = (parts.Count + 1) / 2;
        const string sep = "   ·   ";
        string line1 = string.Join(sep, parts.Take(half));
        string line2 = string.Join(sep, parts.Skip(half));
        return line2.Length > 0 ? line1 + "\n" + line2 : line1;
    }

    // ---------- כניסת שבת/חג והבדלה: מבוססים על isShabbat/holidays שמחזיר getJewishDate ----------

    private static bool HasYomTov(JsonElement jd)
    {
        if (jd.ValueKind != JsonValueKind.Object) return false;
        if (jd.TryGetProperty("holidays", out var hol) && hol.ValueKind == JsonValueKind.Array)
            foreach (var h in hol.EnumerateArray())
                if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("kind", out var k) &&
                    k.ValueKind == JsonValueKind.String && string.Equals(k.GetString(), "yomTov", StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static bool IsHoly(JsonElement jd)
    {
        bool shabbat = jd.ValueKind == JsonValueKind.Object && jd.TryGetProperty("isShabbat", out var s) &&
            (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False) && s.GetBoolean();
        return shabbat || HasYomTov(jd);
    }

    private static DateTime? ParseTimeOnDate(string hm, DateTime date)
    {
        if (string.IsNullOrWhiteSpace(hm)) return null;
        if (DateTime.TryParse(hm, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var t))
            return date.Date + t.TimeOfDay;
        return null;
    }

    private static string CandleHavdalaLine(DateTime now, JsonElement dailyTimes, JsonElement jdToday, JsonElement jdTomorrow)
    {
        bool tomorrowHoly = IsHoly(jdTomorrow);
        bool todayHoly = IsHoly(jdToday);
        if (tomorrowHoly)
        {
            // כניסת שבת/חג: שקיעה פחות מספר דקות הניתן להגדרה (ברירת מחדל 18). אם אין שקיעה במטמון,
            // משתמשים בשדה candleLighting שאוצריא עצמה מחזירה (אז מספר הדקות שהוגדר כאן לא משפיע).
            int minutes = Config.Get("kiosk.candleLightingMinutes", 18);
            DateTime? t = null;
            if (TryPick(dailyTimes, out string sunset, "sunset", "shkia", "shkiah"))
                t = ParseTimeOnDate(sunset, now)?.AddMinutes(-minutes);
            if (t == null && TryPick(dailyTimes, out string cl, "candleLighting", "candleLightingTime", "hadlakatNerot", "candlelighting"))
                t = ParseTimeOnDate(cl, now);
            if (t != null) return Lang.T("הדלקת נרות ", "Candle lighting ") + t.Value.ToString("H:mm");
        }
        else if (todayHoly)
        {
            DateTime? t = null;
            if (TryPick(dailyTimes, out string tz, "tzeit", "tzet", "tzeithakochavim", "nightfall"))
                t = ParseTimeOnDate(tz, now);
            if (t == null && TryPick(dailyTimes, out string hv, "havdalah", "havdala", "motzaeiShabbat"))
                t = ParseTimeOnDate(hv, now);
            if (t != null) return Lang.T("צאת שבת/חג (הבדלה) ", "Havdala ") + t.Value.ToString("H:mm");
        }
        return null;
    }

    // ---------- ספירת העומר: חישוב טהור מתאריך עברי (16 בניסן עד 5 בסיון) ----------

    private static string Norm(string s) => (s ?? "").Replace("\"", "").Replace("'", "").Replace("׳", "").Replace("״", "").Replace(" ", "").Trim();

    private static bool NameIs(string name, params string[] candidates) =>
        candidates.Any(c => string.Equals(Norm(name), Norm(c), StringComparison.OrdinalIgnoreCase));

    private static int OmerDayNumber(JsonElement jd)
    {
        if (jd.ValueKind != JsonValueKind.Object) return 0;
        if (!jd.TryGetProperty("monthName", out var mv) || mv.ValueKind != JsonValueKind.String) return 0;
        if (!jd.TryGetProperty("day", out var dv) || dv.ValueKind != JsonValueKind.Number) return 0;
        string month = mv.GetString();
        int day = dv.GetInt32();
        if (day <= 0) return 0;
        if (NameIs(month, "ניסן", "Nisan", "Nissan") && day >= 16) return day - 15;
        if (NameIs(month, "אייר", "Iyar", "Iyyar")) return 15 + day;
        if (NameIs(month, "סיון", "סיוון", "Sivan") && day <= 5) return 44 + day;
        return 0;
    }

    private static readonly string[] DayWords =
    {
        "", "יום אחד", "שני ימים", "שלושה ימים", "ארבעה ימים", "חמישה ימים", "ששה ימים", "שבעה ימים", "שמונה ימים", "תשעה ימים", "עשרה ימים",
        "אחד עשר יום", "שנים עשר יום", "שלושה עשר יום", "ארבעה עשר יום", "חמישה עשר יום", "ששה עשר יום", "שבעה עשר יום", "שמונה עשר יום", "תשעה עשר יום", "עשרים יום",
        "אחד ועשרים יום", "שנים ועשרים יום", "שלושה ועשרים יום", "ארבעה ועשרים יום", "חמישה ועשרים יום", "ששה ועשרים יום", "שבעה ועשרים יום", "שמונה ועשרים יום", "תשעה ועשרים יום", "שלושים יום",
        "אחד ושלושים יום", "שנים ושלושים יום", "שלושה ושלושים יום", "ארבעה ושלושים יום", "חמישה ושלושים יום", "ששה ושלושים יום", "שבעה ושלושים יום", "שמונה ושלושים יום", "תשעה ושלושים יום", "ארבעים יום",
        "אחד וארבעים יום", "שנים וארבעים יום", "שלושה וארבעים יום", "ארבעה וארבעים יום", "חמישה וארבעים יום", "ששה וארבעים יום", "שבעה וארבעים יום", "שמונה וארבעים יום", "תשעה וארבעים יום",
    };
    private static readonly string[] WeekWords = { "", "שבוע אחד", "שני שבועות", "שלושה שבועות", "ארבעה שבועות", "חמישה שבועות", "ששה שבועות", "שבעה שבועות" };
    private static readonly string[] RemDayWords = { "", "יום אחד", "שני ימים", "שלושה ימים", "ארבעה ימים", "חמישה ימים", "ששה ימים" };

    private static string OmerLine(JsonElement jd)
    {
        int day = OmerDayNumber(jd);
        if (day <= 0 || day > 49) return null;
        string hePhrase;
        int weeks = day / 7, rem = day % 7;
        if (day < 7) hePhrase = "היום " + DayWords[day] + " לעומר";
        else hePhrase = "היום " + DayWords[day] + ", שהם " + WeekWords[weeks] + (rem > 0 ? " ו" + RemDayWords[rem] : "") + " לעומר";
        string enPhrase = "Today is day " + day + " of the Omer" +
            (day >= 7 ? " (" + weeks + " week" + (weeks != 1 ? "s" : "") + (rem > 0 ? " and " + rem + " day" + (rem != 1 ? "s" : "") : "") + ")" : "") + ".";
        return Lang.T(hePhrase, enPhrase);
    }

    // ---------- יום השנה: תאריך עברי כללי שהמנהל מגדיר (לא קשור לתאריך פטירה ספציפי כלשהו) ----------

    private static bool MonthMatches(string a, string b)
    {
        string na = Norm(a), nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) return true;
        // אדר א/אדר ב/אדר (בשנה פשוטה): מקלים ומתאימים כל שילוב, כדי לא להחמיץ את יום השנה
        // בגלל הבדל בין שנה מעוברת לפשוטה. ראו הערה בתיעוד ה-PR.
        bool aAdar = na.StartsWith("אדר") || na.StartsWith("Adar", StringComparison.OrdinalIgnoreCase);
        bool bAdar = nb.StartsWith("אדר") || nb.StartsWith("Adar", StringComparison.OrdinalIgnoreCase);
        return aAdar && bAdar;
    }

    private static string YahrzeitLine(JsonElement jd)
    {
        string month = Config.Get("kiosk.yahrzeitMonth", "");
        int day = Config.Get("kiosk.yahrzeitDay", 0);
        if (string.IsNullOrWhiteSpace(month) || day <= 0) return null;
        if (jd.ValueKind != JsonValueKind.Object) return null;
        if (!jd.TryGetProperty("monthName", out var mv) || mv.ValueKind != JsonValueKind.String) return null;
        if (!jd.TryGetProperty("day", out var dv) || dv.ValueKind != JsonValueKind.Number) return null;
        if (dv.GetInt32() != day || !MonthMatches(month, mv.GetString())) return null;
        string label = Config.Get("kiosk.yahrzeitLabel", "");
        string who = string.IsNullOrWhiteSpace(label) ? "" : " " + label;
        return Lang.T("יום השנה היום" + who, "Yahrzeit today" + who);
    }

    // ---------- גיבוי: שני מנועי חישוב אפשריים, נבחר ב-kiosk.zmanimFallbackEngine ----------
    // ---------- רק כשאין מטמון טרי מאוצריא, ורק אם הוגדר מיקום ----------

    /// <summary>
    /// בוחר בין שני מנועי החישוב לפי kiosk.zmanimFallbackEngine: "noaa" (ברירת המחדל — ההתנהגות
    /// שהייתה כאן עד כה, לא משתנה למתקינים קיימים אלא אם יבחרו אחרת) או "kosherjava" (חדש).
    /// שני המנועים משמשים רק גיבוי כשאין נתונים טריים מאוצריא עצמה.
    /// </summary>
    private static string FallbackLine(DateTime now)
    {
        string engine = Config.Get("kiosk.zmanimFallbackEngine", "noaa");
        return string.Equals(engine, "kosherjava", StringComparison.OrdinalIgnoreCase)
            ? FallbackLineKosherJava(now)
            : FallbackLineNoaa(now);
    }

    /// <summary>
    /// מנוע חדש: מחשב זמני יום דרך ‏ComplexZmanimCalendar (חבילת Zmanim/KosherJava, LGPL — ראו קרדיט
    /// למעלה) ומעביר אותם דרך אותו ZmanimLine/KnownZmanim ששולף מהמטמון של אוצריא — כך הגיבוי יוצא
    /// באותה צורה ואותה בחירת זמנים (kiosk.zmanimShow) כמו הנתיב הרגיל, רק עם מקור חישוב שונה.
    /// זמנים הלכתיים מלאים (גר"א/מג"א) ותיקון גובה אמיתי, אבל קוד צד-שלישי — ייתכנו הבדלים קלים
    /// מהרגיל בזמנים גבוליים מסוימים לעומת מה שהמנהל מכיר.
    /// </summary>
    private static string FallbackLineKosherJava(DateTime now)
    {
        string loc = Config.Get("kiosk.zmanimLocation", "");
        if (string.IsNullOrWhiteSpace(loc)) return null;
        var p = ParseLocation(loc);
        if (p == null) return null;
        double lat = p.Value.lat, lon = p.Value.lon;
        // תיקון גובה (issue #33): מטרים מעל פני הים; 0 כברירת מחדל אם לא הוגדר. הספרייה דורשת ≥ 0
        double elevation = Math.Max(0, Config.Get("kiosk.zmanimElevation", 0.0));

        global::Zmanim.ComplexZmanimCalendar cal;
        try
        {
            var tz = new global::Zmanim.TimeZone.WindowsTimeZone(TimeZoneInfo.Local);
            var geo = new global::Zmanim.Utilities.GeoLocation("kiosk", lat, lon, elevation, tz);
            cal = new global::Zmanim.ComplexZmanimCalendar(now.Date, geo);
        }
        catch { return null; } // קואורדינטות לא תקינות לספרייה (לא אמור לקרות אחרי ParseLocation)

        string H(DateTime? d) => d?.ToString("H:mm", System.Globalization.CultureInfo.InvariantCulture);
        var dict = new Dictionary<string, string>();
        void Add(string key, DateTime? v) { string s = H(v); if (s != null) dict[key] = s; }
        try
        {
            // מפתחות כמו ב-KnownZmanim למעלה (האליאס הראשון בכל שורה), כדי לעבור דרך אותו ZmanimLine
            Add("alotHaShachar", cal.GetAlosHashachar());
            Add("sunrise", cal.GetSunrise());
            Add("sofZmanShmaMGA", cal.GetSofZmanShmaMGA());
            Add("sofZmanShmaGRA", cal.GetSofZmanShmaGRA());
            Add("sofZmanTfilaMGA", cal.GetSofZmanTfilaMGA());
            Add("sofZmanTfilaGRA", cal.GetSofZmanTfilaGRA());
            Add("chatzot", cal.GetChatzos());
            Add("minchaGedola", cal.GetMinchaGedola());
            Add("minchaKetana", cal.GetMinchaKetana());
            Add("plagHamincha", cal.GetPlagHamincha());
            Add("sunset", cal.GetSunset());
            Add("tzeit", cal.GetTzais());
        }
        catch { /* למשל קו רוחב קיצוני עם יום/לילה מתמשכים חלקית — מה שכבר חושב מוצג */ }
        if (dict.Count == 0) return null;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dict));
        string zm = ZmanimLine(doc.RootElement);
        if (zm == null) return null;
        return zm + "\n" + Lang.T(
            "(חישוב גיבוי מקומי — ספריית Zmanim/KosherJava, ללא חיבור ללוח השנה של אוצריא)",
            "(local fallback calculation — Zmanim/KosherJava library, not from Otzaria's calendar)");
    }

    /// <summary>
    /// מנוע ישן (ברירת מחדל, ללא שינוי התנהגות למתקינים קיימים): קירוב אסטרונומי פשוט של NOAA,
    /// הנץ/שקיעה בגובה פני הים בלבד, בלי תיקון גובה וקבוצת זמנים מצומצמת.
    /// </summary>
    private static string FallbackLineNoaa(DateTime now)
    {
        string loc = Config.Get("kiosk.zmanimLocation", "");
        if (string.IsNullOrWhiteSpace(loc)) return null;
        var p = ParseLocation(loc);
        if (p == null) return null;
        double lat = p.Value.lat, lon = p.Value.lon;
        DateTime? T(bool rise, double zenith) => SunTime(now.Date, lat, lon, rise, zenith);
        var rise = T(true, 90.833); var set = T(false, 90.833);
        if (rise == null || set == null) return null;
        var alot = T(true, 90 + 16.1); var tzeit = T(false, 90 + 8.5);
        string H(DateTime? d) => d?.ToString("H:mm") ?? "—";
        var chatzot = rise.Value + TimeSpan.FromTicks((set.Value - rise.Value).Ticks / 2);
        const string sep = "   ·   ";
        return
            Lang.T("עלות השחר ", "Dawn ") + H(alot) + sep + Lang.T("הנץ ", "Sunrise ") + H(rise) + sep +
            Lang.T("חצות ", "Midday ") + H(chatzot) + "\n" +
            Lang.T("שקיעה ", "Sunset ") + H(set) + sep + Lang.T("צאת הכוכבים ", "Nightfall ") + H(tzeit) + sep +
            Lang.T("(חישוב גיבוי, בלי חיבור ללוח השנה של אוצריא)", "(fallback estimate, not from Otzaria's calendar)");
    }

    /// <summary>זמן הנץ/השקיעה בשעון המקומי של המחשב (זנית 90.833°). אלגוריתם NOAA, בלי תיקון גובה</summary>
    private static DateTime? SunTime(DateTime date, double lat, double lon, bool rise, double zenith)
    {
        static double Rad(double d) => d * Math.PI / 180;
        static double Deg(double r) => r * 180 / Math.PI;
        int n = date.DayOfYear;
        double lngHour = lon / 15;
        double t = n + ((rise ? 6 : 18) - lngHour) / 24;
        double M = 0.9856 * t - 3.289;
        double L = M + 1.916 * Math.Sin(Rad(M)) + 0.020 * Math.Sin(Rad(2 * M)) + 282.634;
        L = (L % 360 + 360) % 360;
        double RA = Deg(Math.Atan(0.91764 * Math.Tan(Rad(L))));
        RA = (RA % 360 + 360) % 360;
        RA += Math.Floor(L / 90) * 90 - Math.Floor(RA / 90) * 90;
        RA /= 15;
        double sinDec = 0.39782 * Math.Sin(Rad(L));
        double cosDec = Math.Cos(Math.Asin(sinDec));
        double cosH = (Math.Cos(Rad(zenith)) - sinDec * Math.Sin(Rad(lat))) / (cosDec * Math.Cos(Rad(lat)));
        if (cosH > 1 || cosH < -1) return null;
        double H = rise ? 360 - Deg(Math.Acos(cosH)) : Deg(Math.Acos(cosH));
        H /= 15;
        double T = H + RA - 0.06571 * t - 6.622;
        double UT = ((T - lngHour) % 24 + 24) % 24;
        var utc = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(UT);
        var local = utc.ToLocalTime();
        // תיקון יום: הזמן המקומי צריך ליפול על התאריך המבוקש
        if (local.Date > date.Date) local = local.AddDays(-1);
        else if (local.Date < date.Date) local = local.AddDays(1);
        return local;
    }
}
