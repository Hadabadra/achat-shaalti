using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>C4: חלונות נעילה נוספים ("הוסף זמן") מעבר לחלון הראשי של kiosk.schedule.
/// נשמרים כרשימת "HH:MM-HH:MM" ב-kiosk.schedule.extra. נאכפים בתזמון של הגשר (נעילה רכה);
/// רכיב ההקשחה (שירות) מקבל עדיין רק את החלון הראשי.</summary>
internal static class SchedMulti
{
    private const string Key = "kiosk.schedule.extra";

    public static object[] Extra => Config.GetList(Key)
        .Select(s => s.Split('-')).Where(p => p.Length == 2)
        .Select(p => (object)new { start = p[0], end = p[1] }).ToArray();

    public static bool InAnyWindow(DateTime now) =>
        (Sched.InWindow(Kiosk.ScheduleStart, Kiosk.ScheduleEnd, now) && PackG.WindowDayAllowed(Kiosk.ScheduleStart, now)) ||
        Config.GetList(Key).Select(s => s.Split('-')).Any(p => p.Length == 2 && Sched.InWindow(p[0], p[1], now) && PackG.WindowDayAllowed(p[0], now));

    /// <summary>סוף החלון המתוזמן שבתוכו אנו נמצאים כעת — הרגע שבו המחשב יוצא מכל חלונות הנעילה.
    /// משמש כדי ש"ביטול נעילה" ידני בתוך חלון מתוזמן לא יידרס מיד על ידי התזמון שינעל מחדש.
    /// חלון החוצה חצות מטופל: אם אנו בזנב שאחרי חצות הסוף הוא היום, אחרת מחר.</summary>
    public static DateTime CurrentWindowEnd(DateTime now)
    {
        DateTime best = now;
        void Consider(string start, string end)
        {
            if (!Sched.InWindow(start, end, now)) return;
            var s = Sched.ParseTime(start); var e = Sched.ParseTime(end);
            if (s == null || e == null) return;
            DateTime endAt = e <= s
                ? (now.TimeOfDay < e ? now.Date + e.Value : now.Date.AddDays(1) + e.Value) // חוצה חצות
                : now.Date + e.Value;
            if (endAt > best) best = endAt;
        }
        Consider(Kiosk.ScheduleStart, Kiosk.ScheduleEnd);
        foreach (var p in Config.GetList(Key).Select(s => s.Split('-')).Where(p => p.Length == 2))
            Consider(p[0], p[1]);
        return best;
    }

    public static void Apply(JsonElement sch)
    {
        if (!sch.TryGetProperty("extra", out var ex) || ex.ValueKind != JsonValueKind.Array) return;
        var arr = new JsonArray();
        foreach (var w in ex.EnumerateArray().Take(10))
        {
            string s = w.Str("start"), e = w.Str("end");
            if (!Sched.IsValid(s) || !Sched.IsValid(e)) throw BridgeException.BadRequest(Lang.T("זמן נוסף אינו תקין (HH:MM)", "An additional time is invalid (HH:MM)"));
            arr.Add(s + "-" + e);
        }
        Config.Set(Key, arr);
    }
}
