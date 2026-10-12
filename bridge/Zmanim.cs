namespace OtzariaBridge;

/// <summary>
/// זמני היום לשומר המסך: הנץ ושקיעה גאומטריים (אלגוריתם NOAA, בלי תיקון גובה),
/// וסוף זמן קריאת שמע לפי הגר"א (שלוש שעות זמניות מהנץ). המיקום מוגדר ב-kiosk.zmanimLocation
/// בצורה "קו רוחב, קו אורך". הזמנים להצגה בלבד, ומסומנים כך על המסך.
/// </summary>
internal static class Zmanim
{
    private static DateTime _day;
    private static string _loc;
    private static string _line;

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

    /// <summary>שורה לשומר המסך, או null אם לא הוגדר מיקום</summary>
    public static string Line(DateTime now)
    {
        string loc = Config.Get("kiosk.zmanimLocation", "");
        if (string.IsNullOrWhiteSpace(loc)) return null;
        if (_day == now.Date && _loc == loc) return _line;
        _day = now.Date; _loc = loc; _line = null;
        var p = ParseLocation(loc);
        if (p == null) return null;
        var rise = SunTime(now.Date, p.Value.lat, p.Value.lon, true);
        var set = SunTime(now.Date, p.Value.lat, p.Value.lon, false);
        if (rise == null || set == null) return null;
        var shma = rise.Value + TimeSpan.FromTicks((set.Value - rise.Value).Ticks / 4);
        _line = Lang.T("סוף זמן ק\"ש (גר\"א) ", "Latest Shema (GRA) ") + shma.ToString("H:mm") +
                "   ·   " + Lang.T("שקיעה ", "Sunset ") + set.Value.ToString("H:mm");
        return _line;
    }

    /// <summary>זמן הנץ/השקיעה בשעון המקומי של המחשב (זנית 90.833°)</summary>
    private static DateTime? SunTime(DateTime date, double lat, double lon, bool rise)
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
        double cosH = (Math.Cos(Rad(90.833)) - sinDec * Math.Sin(Rad(lat))) / (cosDec * Math.Cos(Rad(lat)));
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
