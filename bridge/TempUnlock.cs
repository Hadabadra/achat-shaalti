using System.Globalization;

namespace OtzariaBridge;

/// <summary>
/// פתיחה זמנית של כל הנעילה, בסיסמה נפרדת מסיסמת הנעילה הראשית — היגיון טהור וקל לבדיקה,
/// משותף לגשר (Kiosk, בלי רכיב הקשחה) ולרכיב ההקשחה (HardLockService, מקור האמת כשהוא מותקן).
/// משך הפתיחה קבוע (מוגדר מראש בהגדרות, לא נבחר בכל פעם) ומוגבל ל-60 דקות.
/// זמן הסיום נשמר כרגע מוחלט (DateTimeOffset בפורמט "o"), כך שמעבר שעון קיץ/חורף לא מקצר ולא מאריך אותו.
/// </summary>
public static class TempUnlock
{
    public const int MinMinutes = 1, MaxMinutes = 60, DefaultMinutes = 30;

    /// <summary>אורך מינימלי לסיסמת הפתיחה הזמנית (כמו סיסמת הנעילה)</summary>
    public const int MinSecretLength = 6;

    /// <summary>משך הפתיחה מוגבל ל-1–60 דקות</summary>
    public static int ClampMinutes(long minutes) => (int)Math.Clamp(minutes, MinMinutes, MaxMinutes);

    /// <summary>זמן הסיום של פתיחה שמתחילה עכשיו (המשך מוגבל ל-1–60 דקות)</summary>
    public static DateTimeOffset Until(DateTimeOffset now, long minutes) => now.AddMinutes(ClampMinutes(minutes));

    /// <summary>מפענח זמן סיום שנשמר; null אם ריק או לא תקין</summary>
    public static DateTimeOffset? ParseUntil(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    /// <summary>
    /// כמה שניות נשארו לפתיחה (0 = לא פעילה). זמן סיום שרחוק יותר מהמשך המרבי (60 דקות ועוד דקת חסד)
    /// אינו יכול להיווצר בדרך הרגילה — למשל ערך שנערך ידנית או שעון שהוזז אחורה — ולכן נחשב פג: כשל סגור, הנעילה חוזרת.
    /// </summary>
    public static int SecondsLeft(DateTimeOffset? until, DateTimeOffset now)
    {
        if (until == null) return 0;
        var left = until.Value - now;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromMinutes(MaxMinutes + 1)) return 0;
        return (int)Math.Ceiling(left.TotalSeconds);
    }

    public static int SecondsLeft(string iso, DateTimeOffset now) => SecondsLeft(ParseUntil(iso), now);

    /// <summary>תצוגת זמן שנותר: "m:ss" (למשל 29:05)</summary>
    public static string Format(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return (seconds / 60) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }
}
