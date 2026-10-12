using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

public class SchedTests
{
    [Theory]
    [InlineData("20:00", true)]
    [InlineData("08:30", true)]
    [InlineData("0:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)] // מחוץ לטווח היום
    [InlineData("8:5", false)]   // דקות חייבות שתי ספרות
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseTime_validates_format(string input, bool expectValid)
    {
        Assert.Equal(expectValid, Sched.ParseTime(input) != null);
        Assert.Equal(expectValid, Sched.IsValid(input));
    }

    [Fact]
    public void InWindow_same_day_window()
    {
        var day = new DateTime(2026, 1, 1);
        Assert.False(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(8))); // לפני
        Assert.True(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(9)));  // בדיוק ההתחלה — כלול
        Assert.True(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(12))); // באמצע
        Assert.False(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(17))); // בדיוק הסוף — לא כלול
        Assert.False(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(18))); // אחרי
    }

    [Fact]
    public void InWindow_crosses_midnight()
    {
        var day = new DateTime(2026, 1, 1);
        // 20:00–02:00: נעול מ-20:00 בערב עד 02:00 בלילה שאחריו
        Assert.False(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(19)));          // לפני הערב
        Assert.True(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(20)));            // תחילת הערב
        Assert.True(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(23)));            // לפני חצות
        Assert.True(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(24).Add(TimeSpan.FromHours(1)))); // 01:00 שאחרי חצות
        Assert.False(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(24).Add(TimeSpan.FromHours(2)))); // 02:00 — סוף, לא כלול
        Assert.False(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(24).Add(TimeSpan.FromHours(10)))); // צהריים שלמחרת
    }

    [Fact]
    public void InWindow_equal_start_and_end_never_locks()
    {
        // קצה בטוח: לא רוצים שחלון "ריק" ייחשב כל היום
        Assert.False(Sched.InWindow("10:00", "10:00", new DateTime(2026, 1, 1, 10, 0, 0)));
        Assert.False(Sched.InWindow("10:00", "10:00", new DateTime(2026, 1, 1, 23, 59, 0)));
    }

    [Theory]
    [InlineData(null, "10:00")]
    [InlineData("10:00", null)]
    [InlineData("bad", "10:00")]
    [InlineData("10:00", "bad")]
    public void InWindow_invalid_input_never_locks(string start, string end)
    {
        Assert.False(Sched.InWindow(start, end, DateTime.Now));
    }
}
