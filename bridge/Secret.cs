using System.Security.Cryptography;

namespace OtzariaBridge;

/// <summary>
/// קריפטוגרפיית הסיסמה המשותפת לנעילה הרכה (Kiosk, רצה בהרשאות המשתמש) ולרכיב ההקשחה
/// (HardLockService, רץ כ-SYSTEM). עד כה כל אחד הגדיר עותק נפרד וזהה (Hash/Matches/
/// NormalizeRecovery/NewRecoveryCode) — כפילות שיוצרת סיכון שהם יתפצלו בלי ששמים לב
/// (למשל במספר האיטרציות). שני המקומות שומרים את הסיסמה/קוד השחזור בפורמט ובמיקום משלהם,
/// ולכן המחלקה הזו עוסקת רק בחישוב הקריפטוגרפי הטהור ולא בשמירה/קריאה של ההגדרות.
/// </summary>
public static class Secret
{
    public const int Iterations = 200_000;

    public static string Hash(string secret, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32));

    /// <summary>בודק אם secret תואם לגיבוב/salt שמורים (Base64). false גם כשחסר/לא תקין — לעולם לא זורק</summary>
    public static bool Matches(string secret, string hashB64, string saltB64)
    {
        if (string.IsNullOrEmpty(hashB64) || string.IsNullOrEmpty(saltB64) || string.IsNullOrEmpty(secret)) return false;
        try
        {
            byte[] expected = Convert.FromBase64String(hashB64);
            byte[] actual = Convert.FromBase64String(Hash(secret, Convert.FromBase64String(saltB64)));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; } // גיבוב/salt שמורים בפורמט פגום — נחשב "לא תואם", לא קריסה
    }

    /// <summary>מנרמל קוד שחזור שהוקלד: בלי מקפים/רווחים, אותיות גדולות (תואם לאלפבית ב-NewRecoveryCode)</summary>
    public static string NormalizeRecovery(string s) => (s ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

    public static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // בלי I/O/0/1 (דמיון חזותי)
        return new string(Enumerable.Range(0, 12).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }
}
