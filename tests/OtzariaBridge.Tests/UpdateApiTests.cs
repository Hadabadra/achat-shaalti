using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>
/// השוואת גרסאות העדכון. רקע: בדיקת העדכון חיפשה תגים בשם שגוי וב-1.6→1.7 תמיד דיווחה
/// "מעודכן" — באג שהתגלה רק בשטח. בדיקות אלה נועדו לתפוס רגרסיה דומה בהשוואת הגרסאות עצמה.
/// </summary>
public class UpdateApiTests
{
    [Theory]
    [InlineData("v1.7.0", 1, 7, 0)]
    [InlineData("1.7.0", 1, 7, 0)]
    [InlineData("v2.0", 2, 0, 0)] // בלי patch — ברירת מחדל 0
    [InlineData("bridge-v1.6.2", 1, 6, 2)] // פורמט תג ישן
    public void Parse_extracts_version_numbers(string tag, int major, int minor, int patch)
    {
        var v = UpdateApi.Parse(tag);
        Assert.NotNull(v);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("draft")]
    public void Parse_returns_null_for_non_version_text(string tag)
    {
        Assert.Null(UpdateApi.Parse(tag));
    }

    [Fact]
    public void Version_comparison_detects_newer()
    {
        // ליבת הבאג ההיסטורי: 1.7.0 חייבת להיחשב "חדשה יותר" מ-1.6.0
        Assert.True(UpdateApi.Parse("v1.7.0") > UpdateApi.Parse("v1.6.0"));
        Assert.False(UpdateApi.Parse("v1.6.0") > UpdateApi.Parse("v1.7.0"));
        Assert.False(UpdateApi.Parse("v1.7.0") > UpdateApi.Parse("v1.7.0"));
        Assert.True(UpdateApi.Parse("v2.0.0") > UpdateApi.Parse("v1.99.99"));
    }
}

/// <summary>
/// נורמליזציית קוד שחזור: המשתמש עשוי להקליד עם מקפים ("XXXX-XXXX-XXXX"), רווחים, או אותיות קטנות —
/// חייבים כולם להצטמצם לאותו מחרוזת שנשמרה בזמן היצירה (ראו Secret.NewRecoveryCode).
/// </summary>
public class RecoveryNormalizationTests
{
    [Theory]
    [InlineData("ABCD-EFGH-2345", "ABCDEFGH2345")]
    [InlineData("abcd-efgh-2345", "ABCDEFGH2345")]
    [InlineData("ABCD EFGH 2345", "ABCDEFGH2345")]
    [InlineData("ABCDEFGH2345", "ABCDEFGH2345")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeRecovery_strips_separators_and_uppercases(string input, string expected)
    {
        Assert.Equal(expected, Secret.NormalizeRecovery(input));
    }

    [Fact]
    public void NormalizeRecovery_is_idempotent()
    {
        string once = Secret.NormalizeRecovery("abcd-efgh-2345");
        string twice = Secret.NormalizeRecovery(once);
        Assert.Equal(once, twice);
    }
}
