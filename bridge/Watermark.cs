using System.Runtime.InteropServices;

namespace OtzariaBridge;

/// <summary>
/// סמל "אחת שאלתי" קבוע ושקוף-למחצה בפינה השמאלית התחתונה (בדומה לסימן של סינון), מעל כל החלונות,
/// בלי לקבל לחיצות. כבוי כברירת מחדל; הפעלה/כיבוי וגודל מתפריט המגש או /watermark/set.
/// config: watermark.enabled (false), watermark.size (48/72/112).
/// </summary>
internal static class Watermark
{
    private static Form _f;
    public static readonly int[] Sizes = { 48, 72, 112 };

    public static void Init()
    {
        Server.Route("/watermark/get", _ => State());
        Server.Route("/watermark/set", b =>
        {
            if (b.TryGetProperty("enabled", out var e) && e.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                Config.Set("watermark.enabled", e.GetBoolean());
            if (b.TryGetProperty("size", out var s) && s.TryGetInt32(out int n) && Sizes.Contains(n)) Config.Set("watermark.size", n);
            Config.Save();
            Apply();
            return State();
        });
        Apply();
    }

    private static object State() => new { enabled = Config.Get("watermark.enabled", false), size = Config.Get("watermark.size", 72), sizes = Sizes };

    public static void Toggle() { Config.Set("watermark.enabled", !Config.Get("watermark.enabled", false)); Config.Save(); Apply(); }
    public static void SetSize(int n) { Config.Set("watermark.size", n); Config.Save(); Apply(); }

    public static void Apply() => Tray.Invoke(() =>
    {
        _f?.Close(); _f = null;
        if (!Config.Get("watermark.enabled", false)) return;
        int size = Config.Get("watermark.size", 72);
        Bitmap bmp;
        try { bmp = new Icon(Overlay.AppIcon(), 256, 256).ToBitmap(); } catch { return; }
        _f = new ClickThrough { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, Opacity = 0.35,
            StartPosition = FormStartPosition.Manual, BackColor = Color.Magenta, TransparencyKey = Color.Magenta, Size = new Size(size, size) };
        _f.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = bmp, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Magenta });
        var wa = Screen.PrimaryScreen!.WorkingArea;
        _f.Location = new Point(wa.Left + 12, wa.Bottom - size - 12);
        _f.Show();
    });

    private sealed class ClickThrough : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80000 | 0x80 | 0x08000000; // TRANSPARENT | LAYERED | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }
    }
}
