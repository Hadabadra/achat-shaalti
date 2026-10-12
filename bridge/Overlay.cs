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
    /// <summary>מסך המנוחה מוצג בגלל חוסר פעילות (ולא בגלל מזעור) — לא להסתיר אותו כשאוצריא לא ממוזערת</summary>
    public static bool RestIdle => _rest is { Visible: true, Idle: true };

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

    public static void ShowRest(IntPtr otzaria, bool idle = false) => Tray.Invoke(() =>
    {
        _rest ??= new RestScreen();
        _rest.Target = otzaria;
        if (!_rest.Visible) _rest.Idle = idle;
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
/// מסך מנוחה כשאוצריא ממוזערת: "לימוד ללא הפרעות", פסוק, שעה ותאריך עברי, ונר הנצחה מרצד עם נוסח ההנצחה.
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
    public bool Idle;

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

        // רקע: לילה עמוק וחם, משתנה לאט מאוד
        float k = (float)(Math.Sin(t / 22.0) * 0.5 + 0.5);
        Color c1 = Mix(Color.FromArgb(22, 26, 44), Color.FromArgb(30, 24, 40), k);
        Color c2 = Mix(Color.FromArgb(8, 9, 16), Color.FromArgb(12, 10, 14), k);
        using (var bg = new LinearGradientBrush(r, c1, c2, 90f)) g.FillRectangle(bg, r);

        // כוכבים עדינים בלבד (בלי אותיות), מנצנצים ועולים לאט מאוד
        for (int i = 0; i < _parts.Length; i++)
        {
            ref var p = ref _parts[i];
            p.Y -= p.Speed * 0.012f;
            if (p.Y < -0.05f) { p = NewParticle(false); p.Ch = '\0'; }
            float px = p.X * r.Width, py = p.Y * r.Height;
            int a = (int)(25 + 55 * (Math.Sin(t * 0.6f + p.Phase) * 0.5 + 0.5));
            float d = p.Size * 2.2f * s;
            using var sb = new SolidBrush(Color.FromArgb(a, 225, 215, 190));
            g.FillEllipse(sb, px, py, d, d);
        }

        // כל התוכן נע לאט מאוד (מניעת צריבה)
        float dx = (float)Math.Sin(t * 0.04) * r.Width * 0.04f;
        float dy = (float)Math.Sin(t * 0.07) * r.Height * 0.025f;
        float cx = r.Width / 2f + dx;
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center };
        var center = new StringFormat { Alignment = StringAlignment.Center };
        Color gold = Color.FromArgb(232, 200, 140);
        using var goldB = new SolidBrush(gold);
        using var dim = new SolidBrush(Color.FromArgb(170, 220, 214, 200));
        using var headF = new Font("David", 40f, FontStyle.Bold, GraphicsUnit.Point);
        using var verseF = new Font("David", 22f, GraphicsUnit.Point);
        using var timeF = new Font("Segoe UI Light", 64f, GraphicsUnit.Point);
        using var dateF = new Font("David", 20f, GraphicsUnit.Point);
        using var dedF = new Font("David", 17f, GraphicsUnit.Point);
        using var dedNameF = new Font("David", 21f, FontStyle.Bold, GraphicsUnit.Point);
        using var hintF = new Font("Segoe UI", 11f, GraphicsUnit.Point);
        float W = r.Width;
        RectangleF Line(float y, float h) => new(cx - W / 2f, y, W, h);

        float y = r.Height * 0.09f + dy;
        g.DrawString(Lang.T("לימוד ללא הפרעות", "Learning without distractions"), headF, goldB, Line(y, 70 * s), rtl);
        y += 70 * s;
        // קו מעוטר דק
        using (var pen = new Pen(Color.FromArgb(120, gold), 1.2f * s))
        {
            g.DrawLine(pen, cx - 150 * s, y, cx - 12 * s, y);
            g.DrawLine(pen, cx + 12 * s, y, cx + 150 * s, y);
            g.FillEllipse(goldB, cx - 4 * s, y - 4 * s, 8 * s, 8 * s);
        }
        y += 14 * s;
        g.DrawString("וְהָגִיתָ בּוֹ יוֹמָם וָלַיְלָה", verseF, dim, Line(y, 40 * s), rtl);
        y += 58 * s;

        var now = DateTime.Now;
        g.DrawString(now.ToString("HH:mm"), timeF, Brushes.White, Line(y, 110 * s), center);
        y += 108 * s;
        g.DrawString(Overlay.HebrewDate(now), dateF, Brushes.White, Line(y, 40 * s), rtl);
        y += 36 * s;
        g.DrawString(now.ToString("d/M/yyyy"), hintF, dim, Line(y, 30 * s), center);
        string zm = Zmanim.Line(now);
        if (zm != null)
        {
            y += 34 * s;
            g.DrawString(zm, dateF, goldB, Line(y, 40 * s), rtl);
        }

        // נר הנצחה בתחתית, עם להבה מרצדת והילה חמה
        float baseY = r.Height * 0.86f + dy;
        float candleW = 26 * s, candleH = 92 * s;
        float candleX = cx - candleW / 2f, candleTop = baseY - candleH;
        float flick = (float)(Math.Sin(t * 7.3) * 0.5 + Math.Sin(t * 11.1 + 1) * 0.3 + Math.Sin(t * 3.1) * 0.2);
        float fh = (34 + flick * 4) * s, fw = (15 + flick * 1.2f) * s;
        float fx = cx + flick * 1.5f * s, fy = candleTop - 6 * s;
        // הילה
        for (int i = 6; i >= 1; i--)
        {
            float rad = (40 + i * 26) * s * (1 + flick * 0.03f);
            using var glow = new SolidBrush(Color.FromArgb((int)(10 + 4 * (6 - i)), 255, 190, 110));
            g.FillEllipse(glow, fx - rad, fy - fh * 0.5f - rad, rad * 2, rad * 2);
        }
        // גוף הנר
        var body = new RectangleF(candleX, candleTop, candleW, candleH);
        using (var cb = new LinearGradientBrush(body, Color.FromArgb(250, 244, 230), Color.FromArgb(200, 186, 160), 0f))
            g.FillRectangle(cb, body);
        using (var top = new SolidBrush(Color.FromArgb(255, 252, 240)))
            g.FillEllipse(top, candleX, candleTop - 4 * s, candleW, 8 * s);
        // פתיל
        using (var wick = new Pen(Color.FromArgb(60, 45, 35), 2f * s)) g.DrawLine(wick, cx, candleTop, cx, candleTop - 7 * s);
        // להבה: חיצונית כתומה ופנימית בהירה
        using (var flame = new GraphicsPath())
        {
            flame.AddBezier(fx, fy - fh, fx + fw * 0.9f, fy - fh * 0.45f, fx + fw * 0.55f, fy, fx, fy);
            flame.AddBezier(fx, fy, fx - fw * 0.55f, fy, fx - fw * 0.9f, fy - fh * 0.45f, fx, fy - fh);
            using var fb = new PathGradientBrush(flame) { CenterColor = Color.FromArgb(255, 255, 245, 200), SurroundColors = new[] { Color.FromArgb(230, 255, 150, 40) } };
            fb.CenterPoint = new PointF(fx, fy - fh * 0.3f);
            g.FillPath(fb, flame);
        }
        // מעמד
        using (var stand = new SolidBrush(Color.FromArgb(150, 120, 80)))
        using (var sp = Overlay.Round(new Rectangle((int)(cx - 46 * s), (int)baseY, (int)(92 * s), (int)(10 * s)), (int)(5 * s)))
            g.FillPath(stand, sp);

        // נוסח ההנצחה מעל הנר
        float ty = candleTop - fh - 150 * s;
        g.DrawString("לעילוי נשמת הנערה הצדקנית", dedF, dim, Line(ty, 32 * s), rtl);
        g.DrawString("אסתר גיטה דינר ע\"ה", dedNameF, goldB, Line(ty + 32 * s, 40 * s), rtl);
        g.DrawString("בת רבי מיכאל שלום הלוי שיבדלט\"א · תנצב\"ה", dedF, dim, Line(ty + 72 * s, 32 * s), rtl);

        // רמז קטן בתחתית; אין צורך בכפתור — כל לחיצה חוזרת
        _btn = Rectangle.Empty;
        g.DrawString(Lang.T("לחיצה או מקש כלשהו — חזרה לאוצריא", "Click or press any key to return to Otzaria"), hintF, dim,
            Line(r.Height - 40 * s, 30 * s), rtl);
    }

    private static Color Mix(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _icon.Dispose(); _logo.Dispose(); }
        base.Dispose(disposing);
    }
}
