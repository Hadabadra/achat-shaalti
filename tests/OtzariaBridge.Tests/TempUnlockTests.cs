using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>
/// פתיחה זמנית: המשך מוגבל ל-1–60 דקות, וחישוב הזמן שנותר "נכשל סגור" — זמן סיום פגום, שעבר,
/// או רחוק מהמשך המרבי (ערך שנערך ידנית או שעון שהוזז אחורה) נחשב כפתיחה שהסתיימה, והנעילה חוזרת.
/// </summary>
public class TempUnlockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(100000, 60)]
    public void ClampMinutes_limits_to_1_through_60(long input, int expected)
    {
        Assert.Equal(expected, TempUnlock.ClampMinutes(input));
    }

    [Fact]
    public void Until_adds_clamped_minutes()
    {
        Assert.Equal(Now.AddMinutes(30), TempUnlock.Until(Now, 30));
        Assert.Equal(Now.AddMinutes(60), TempUnlock.Until(Now, 600));
    }

    [Fact]
    public void SecondsLeft_counts_down_and_rounds_up()
    {
        var until = Now.AddMinutes(30);
        Assert.Equal(1800, TempUnlock.SecondsLeft(until, Now));
        Assert.Equal(1, TempUnlock.SecondsLeft(until, until.AddMilliseconds(-200)));
    }

    [Theory]
    [InlineData(0)]      // בדיוק בזמן הסיום
    [InlineData(-1)]     // אחרי הסיום
    [InlineData(62)]     // רחוק מהמשך המרבי (60 + דקת חסד) — נחשב פג
    [InlineData(60 * 24 * 365)]
    public void SecondsLeft_is_zero_when_expired_or_implausibly_far(int minutesAhead)
    {
        Assert.Equal(0, TempUnlock.SecondsLeft(Now.AddMinutes(minutesAhead), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void SecondsLeft_is_zero_for_missing_or_bad_value(string iso)
    {
        Assert.Equal(0, TempUnlock.SecondsLeft(iso, Now));
    }

    [Fact]
    public void Stored_value_round_trips_as_absolute_instant()
    {
        // נשמר כ-"o" (רגע מוחלט עם היסט) — אותו רגע גם כשמפענחים באזור זמן אחר / אחרי מעבר שעון
        var until = TempUnlock.Until(Now, 45);
        string iso = until.ToString("o");
        Assert.Equal(until, TempUnlock.ParseUntil(iso));
        Assert.Equal(45 * 60, TempUnlock.SecondsLeft(iso, Now.ToUniversalTime()));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(3600, "60:00")]
    [InlineData(-3, "0:00")]
    public void Format_is_minutes_and_two_digit_seconds(int seconds, string expected)
    {
        Assert.Equal(expected, TempUnlock.Format(seconds));
    }
}
