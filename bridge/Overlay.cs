using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OtzariaBridge;

/// <summary>
/// חלונות הנעילה (חוט המגש בלבד):
/// שעון צף — שעה, תאריך עברי ולועזי וסוללה, כי שורת המשימות מוסתרת בנעילה. לחיצה עליו מחזירה לאוצריא.
/// מסך מנוחה — כשאוצריא ממוזערת: מסך מלא עם סמל אוצריא, שעה ותאריך, ולחיצה בכל מקום מחזירה לאוצריא.
/// </summary>
public static class Overlay
{
    private static ClockBar _clock;
    private static RestScreen _rest;

    public static bool RestVisible => _rest is { Visible: true };

    public static string HebrewDate(DateTime d)
    {
        try
        {
            var he = new CultureInfo("he-IL");
            he.DateTimeFormat.Calendar = new HebrewCalendar();
            return d.ToString("dddd, d MMMM yyyy", he);
        }
        catch { return d.ToString("dddd d/M/yyyy"); }
    }

    public static void ShowClock() => Tray.Invoke(() =>
    {
        if (!Config.Get("kiosk.clock", true)) { HideClock(); return; }
        _clock ??= new ClockBar();
        _clock.Place();
        if (!_clock.Visible) _clock.Show();
    });

    public static void HideClock() => Tray.Invoke(() => { _clock?.Hide(); });

    public static void ShowRest(IntPtr otzaria) => Tray.Invoke(() =>
    {
        _rest ??= new RestScreen();
        _rest.Target = otzaria;
        if (!_rest.Visible) { _rest.Bounds = Screen.PrimaryScreen!.Bounds; _rest.Show(); _rest.Activate(); }
    });

    public static void HideRest() => Tray.Invoke(() => { if (_rest is { Visible: true }) _rest.Hide(); });

    private static SideBar _side;

    public static void ShowSideBar() => Tray.Invoke(() =>
    {
        if (!Config.Get("kiosk.sideBar", true)) { HideSideBar(); return; }
        _side ??= new SideBar();
        _side.Rebuild();
    });

    public static void HideSideBar() => Tray.Invoke(() => { _side?.Hide(); });

    public static void HideAll() { HideRest(); HideClock(); HideSideBar(); }

    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);

    internal static void BringOtzaria(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            string path = Kiosk.OtzariaPath();
            string name = string.IsNullOrEmpty(path) ? "otzaria" : Path.GetFileNameWithoutExtension(path);
            hwnd = Process.GetProcessesByName(name).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        }
        if (hwnd == IntPtr.Zero) return;
        Kiosk.BringToFront(hwnd);
    }

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static Icon AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }
}

/// <summary>שעון צף קטן, תמיד מעל, בלי לקחת פוקוס מאוצריא</summary>
internal sealed class ClockBar : Form
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private string _time = "", _date = "", _heb = "", _bat = "";

    public ClockBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 32, 38);
        ForeColor = Color.White;
        Opacity = 0.92;
        DoubleBuffered = true;
        Size = new Size(ScaleX(250), ScaleX(64));
        Cursor = Cursors.Hand;
        RightToLeft = RightToLeft.Yes;
        _tick.Tick += (_, _) => Refresh_();
        Click += (_, _) => Overlay.BringOtzaria(IntPtr.Zero);
        Refresh_();
        _tick.Start();
    }

    private int ScaleX(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    public void Place()
    {
        var wa = Screen.PrimaryScreen!.Bounds;
        int m = ScaleX(12);
        string pos = Config.Get("kiosk.clockPosition", "bottom-left");
        int x = pos.EndsWith("right") ? wa.Right - Width - m : wa.Left + m;
        int y = pos.StartsWith("top") ? wa.Top + m : wa.Bottom - Height - m;
        Location = new Point(x, y);
        Region = new Region(Overlay.Round(new Rectangle(0, 0, Width, Height), ScaleX(12)));
    }

    private void Refresh_()
    {
        var now = DateTime.Now;
        _time = now.ToString("HH:mm");
        _date = now.ToString("d/M/yyyy");
        _heb = Overlay.HebrewDate(now);
        _bat = "";
        try
        {
            var ps = SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown)
                _bat = "🔋 " + (int)Math.Round(ps.BatteryLifePercent * 100) + "%" + (ps.PowerLineStatus == PowerLineStatus.Online ? " ⚡" : "");
        }
        catch { }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var big = new Font("Segoe UI Semibold", 20f, GraphicsUnit.Point);
        using var small = new Font("Segoe UI", 9f, GraphicsUnit.Point);
        using var dim = new SolidBrush(Color.FromArgb(200, 220, 225, 232));
        var r = ClientRectangle;
        int pad = ScaleX(12);
        var timeSize = g.MeasureString(_time, big);
        g.DrawString(_time, big, Brushes.White, r.Right - pad - timeSize.Width, (r.Height - timeSize.Height) / 2f);
        var sf = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near };
        var textRect = new RectangleF(pad, ScaleX(8), r.Width - pad * 2 - timeSize.Width - ScaleX(6), ScaleX(22));
        g.DrawString(_heb, small, Brushes.White, textRect, sf);
        textRect.Y += ScaleX(24);
        g.DrawString(_date + (_bat.Length > 0 ? "   " + _bat : ""), small, dim, textRect, sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// מסך מנוחה כשאוצריא ממוזערת: סמל אוצריא גדול וברור בתוך עיגול זוהר פועם, שעה ותאריך, אותיות וכוכבים צפים.
/// הכול נע לאט (גם הבלוק המרכזי) כדי לא לצרוב את המסך. לחיצה או מקש מחזירים לאוצריא.
/// </summary>
internal sealed class RestScreen : Form
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 40 };
    private readonly Icon _icon = Overlay.AppIcon();
    private readonly Bitmap _logo;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rnd = new();
    private readonly Particle[] _parts = new Particle[70];
    private const string Letters = "אבגדהוזחטיכלמנסעפצקרשת";
    private Rectangle _btn;
    public IntPtr Target;

    private struct Particle { public float X, Y, Size, Speed, Phase; public char Ch; }

    public RestScreen()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(14, 20, 30);
        Cursor = Cursors.Hand;
        try { _logo = new Icon(_icon, 256, 256).ToBitmap(); } catch { _logo = SystemIcons.Application.ToBitmap(); }
        for (int i = 0; i < _parts.Length; i++) _parts[i] = NewParticle(true);
        _tick.Tick += (_, _) => Invalidate();
        MouseClick += (_, _) => Back();
        KeyDown += (_, _) => Back();
    }

    private Particle NewParticle(bool anywhere) => new()
    {
        X = (float)_rnd.NextDouble(),
        Y = anywhere ? (float)_rnd.NextDouble() : 1.05f,
        Size = 0.6f + (float)_rnd.NextDouble() * 1.4f,
        Speed = 0.004f + (float)_rnd.NextDouble() * 0.012f,
        Phase = (float)(_rnd.NextDouble() * Math.PI * 2),
        Ch = _rnd.Next(5) == 0 ? Letters[_rnd.Next(Letters.Length)] : '\0',
    };

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _tick.Start(); else _tick.Stop();
    }

    private void Back()
    {
        Hide();
        Overlay.BringOtzaria(Target);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        float t = (float)_clock.Elapsed.TotalSeconds;
        float s = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // רקע: מעבר צבע איטי בין כחול עמוק לסגול-כחול
        float k = (float)(Math.Sin(t / 18.0) * 0.5 + 0.5);
        Color c1 = Mix(Color.FromArgb(18, 34, 58), Color.FromArgb(34, 24, 62), k);
        Color c2 = Mix(Color.FromArgb(8, 12, 20), Color.FromArgb(10, 18, 30), k);
        using (var bg = new LinearGradientBrush(r, c1, c2, 90f)) g.FillRectangle(bg, r);

        // כוכבים ואותיות צפים כלפי מעלה
        using (var letterFont = new Font("Segoe UI", 18f * s, GraphicsUnit.Pixel))
        {
            for (int i = 0; i < _parts.Length; i++)
            {
                ref var p = ref _parts[i];
                p.Y -= p.Speed * 0.04f;
                if (p.Y < -0.05f) p = NewParticle(false);
                float px = (p.X + (float)Math.Sin(t * 0.3f + p.Phase) * 0.012f) * r.Width;
                float py = p.Y * r.Height;
                int a = (int)(40 + 70 * (Math.Sin(t * 0.8f + p.Phase) * 0.5 + 0.5));
                if (p.Ch != '\0')
                {
                    using var lb = new SolidBrush(Color.FromArgb(a / 2, 200, 215, 255));
                    g.DrawString(p.Ch.ToString(), letterFont, lb, px, py);
                }
                else
                {
                    float d = p.Size * 3.2f * s;
                    using var sb = new SolidBrush(Color.FromArgb(a, 190, 210, 255));
                    g.FillEllipse(sb, px, py, d, d);
                }
            }
        }

        // הבלוק המרכזי נע לאט בצורת 8 (מניעת צריבה)
        float dx = (float)Math.Sin(t * 0.05) * r.Width * 0.06f;
        float dy = (float)Math.Sin(t * 0.09) * r.Height * 0.035f;
        float cx = r.Width / 2f + dx;
        float iconSize = 168 * s;
        float top = r.Height * 0.2f + dy;
        float icy = top + iconSize / 2f;

        // זוהר פועם: שלוש טבעות
        for (int i = 0; i < 3; i++)
        {
            float ph = (t * 0.45f + i / 3f) % 1f;
            float rad = iconSize * (0.75f + ph * 0.9f);
            int a = (int)(70 * (1 - ph));
            using var pen = new Pen(Color.FromArgb(a, 130, 175, 255), 2f * s);
            g.DrawEllipse(pen, cx - rad, icy - rad, rad * 2, rad * 2);
        }
        // לוח עגול בהיר מאחורי הסמל, כדי שיהיה ברור גם כשהסמל כהה
        float tile = iconSize * 1.28f;
        using (var halo = new SolidBrush(Color.FromArgb(40, 160, 200, 255)))
            g.FillEllipse(halo, cx - tile * 0.62f, icy - tile * 0.62f, tile * 1.24f, tile * 1.24f);
        using (var disc = new SolidBrush(Color.FromArgb(245, 247, 252)))
            g.FillEllipse(disc, cx - tile / 2, icy - tile / 2, tile, tile);
        g.DrawImage(_logo, cx - iconSize / 2, icy - iconSize / 2, iconSize, iconSize);

        using var nameF = new Font("Segoe UI Semibold", 26f, GraphicsUnit.Point);
        using var timeF = new Font("Segoe UI Light", 76f, GraphicsUnit.Point);
        using var dateF = new Font("Segoe UI", 20f, GraphicsUnit.Point);
        using var hintF = new Font("Segoe UI", 13f, GraphicsUnit.Point);
        using var dim = new SolidBrush(Color.FromArgb(190, 210, 218, 228));
        var center = new StringFormat { Alignment = StringAlignment.Center };
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center };

        float y = icy + tile / 2 + 14 * s;
        g.DrawString(Lang.T("אוצריא", "Otzaria"), nameF, Brushes.White, new RectangleF(cx - r.Width / 2f, y, r.Width, 50 * s), rtl);
        y += 58 * s;
        var now = DateTime.Now;
        g.DrawString(now.ToString("HH:mm"), timeF, Brushes.White, new RectangleF(cx - r.Width / 2f, y, r.Width, 140 * s), center);
        y += 138 * s;
        g.DrawString(Overlay.HebrewDate(now), dateF, Brushes.White, new RectangleF(cx - r.Width / 2f, y, r.Width, 44 * s), rtl);
        y += 42 * s;
        g.DrawString(now.ToString("d/M/yyyy"), dateF, dim, new RectangleF(cx - r.Width / 2f, y, r.Width, 44 * s), center);
        y += 72 * s;

        string label = Lang.T("חזרה לאוצריא", "Back to Otzaria");
        var sz = g.MeasureString(label, dateF);
        _btn = new Rectangle((int)(cx - sz.Width / 2 - 34 * s), (int)y, (int)(sz.Width + 68 * s), (int)(sz.Height + 22 * s));
        using (var path = Overlay.Round(_btn, (int)(_btn.Height / 2f)))
        using (var fill = new LinearGradientBrush(_btn, Color.FromArgb(255, 92, 150, 220), Color.FromArgb(255, 62, 108, 186), 90f))
            g.FillPath(fill, path);
        g.DrawString(label, dateF, Brushes.White, new RectangleF(_btn.X, _btn.Y + 11 * s, _btn.Width, _btn.Height), rtl);
        g.DrawString(Lang.T("לחצו בכל מקום או על מקש כלשהו", "Click anywhere or press any key"), hintF, dim,
            new RectangleF(cx - r.Width / 2f, _btn.Bottom + 16 * s, r.Width, 30 * s), rtl);
    }

    private static Color Mix(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _icon.Dispose(); _logo.Dispose(); }
        base.Dispose(disposing);
    }
}
