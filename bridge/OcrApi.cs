using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OtzariaBridge;

/// <summary>
/// נקודות הקצה /ocr/* — מנוע זיהוי הטקסט המובנה של Windows (Windows.Media.Ocr).
/// לעברית נדרשת חבילת השפה "Language.OCR~~~he-IL" (הגדרות ← זמן ושפה ← שפה ← עברית ← אפשרויות).
/// </summary>
public static class OcrApi
{
    public const string HebrewCapability = "Language.OCR~~~he-IL";

    public static void Register()
    {
        Server.Route("/ocr/status", _ => Status());
        Server.Route("/ocr/recognize", Recognize);
    }

    private static List<string> AvailableTags()
    {
        var res = new List<string>();
        try
        {
            foreach (var l in OcrEngine.AvailableRecognizerLanguages) res.Add(l.LanguageTag);
        }
        catch { /* מנוע לא זמין */ }
        return res;
    }

    private static bool IsHebrew(string tag) =>
        tag != null && (tag.Equals("he", StringComparison.OrdinalIgnoreCase) ||
                        tag.StartsWith("he-", StringComparison.OrdinalIgnoreCase));

    private static object Status()
    {
        var tags = AvailableTags();
        return new
        {
            available = tags.Count > 0,
            languages = tags,
            hebrew = tags.Exists(IsHebrew),
            hebrewCapability = HebrewCapability,
            installHint = "Add-WindowsCapability -Online -Name " + HebrewCapability,
            maxImageDimension = SafeMaxDimension(),
        };
    }

    private static int SafeMaxDimension()
    {
        try { return (int)OcrEngine.MaxImageDimension; } catch { return 10000; }
    }

    private static OcrEngine CreateEngine(string lang)
    {
        OcrEngine engine = null;
        if (!string.IsNullOrWhiteSpace(lang))
        {
            try { engine = OcrEngine.TryCreateFromLanguage(new Language(lang)); }
            catch (ArgumentException) { throw BridgeException.BadRequest("קוד שפה לא תקין: " + lang); }
            if (engine == null)
                throw BridgeException.Unsupported(IsHebrew(lang)
                    ? "חבילת השפה של זיהוי הטקסט בעברית אינה מותקנת ב-Windows (" + HebrewCapability + ")"
                    : "חבילת השפה של זיהוי הטקסט אינה מותקנת: " + lang);
            return engine;
        }
        engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
            throw BridgeException.Unsupported("אין חבילת שפה לזיהוי טקסט ב-Windows");
        return engine;
    }

    private static byte[] DecodeImage(string s)
    {
        int comma = s.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            s = s.Substring(comma + 7);
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw BridgeException.BadRequest("השדה image אינו base64 תקין"); }
    }

    /// <summary>גוף: { image: base64 או data URL (PNG/JPEG/BMP), lang?: "he-IL" } → { text, lines: [{ text, words: [{ text, x, y, w, h }] }], width, height, lang }</summary>
    private static async Task<object> Recognize(JsonElement b)
    {
        string image = b.Req("image");
        string lang = b.Str("lang");
        var engine = CreateEngine(lang);
        byte[] bytes = DecodeImage(image);
        if (bytes.Length == 0) throw BridgeException.BadRequest("התמונה ריקה");

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        BitmapDecoder decoder;
        try { decoder = await BitmapDecoder.CreateAsync(stream); }
        catch (Exception) { throw BridgeException.BadRequest("פורמט התמונה אינו נתמך"); }

        int max = SafeMaxDimension();
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
            throw BridgeException.BadRequest("התמונה גדולה מדי (מקסימום " + max + " פיקסלים בכל צד)");

        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<object>();
        foreach (var line in result.Lines)
        {
            var words = new List<object>();
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new { text = w.Text, x = r.X, y = r.Y, w = r.Width, h = r.Height });
            }
            lines.Add(new { text = line.Text, words });
        }

        return new
        {
            text = result.Text,
            lines,
            width = (int)decoder.PixelWidth,
            height = (int)decoder.PixelHeight,
            lang = engine.RecognizerLanguage.LanguageTag,
        };
    }
}
