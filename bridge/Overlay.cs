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

    public static void HideAll() { HideRest(); HideClock(); }

    private static LockCountdown _countdown;

    /// <summary>אזהרת ספירה-לאחור לפני נעילה מתוזמנת (5 דקות ודקה לפני)</summary>
    public static void ShowCountdown(DateTime target) => Tray.Invoke(() =>
    {
        _countdown ??= new LockCountdown();
        _countdown.SetTarget(target);
        if (!_countdown.Visible) { _countdown.Show(); _countdown.Activate(); }
    });

    public static void HideCountdown() => Tray.Invoke(() => { if (_countdown is { Visible: true }) _countdown.Hide(); });

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
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
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

/// <summary>מסך מנוחה כשאוצריא ממוזערת: סמל, שעה ותאריך; לחיצה או מקש מחזירים לאוצריא</summary>
internal sealed class RestScreen : Form
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly Icon _icon = Overlay.AppIcon();
    private Bitmap _iconBmp;
    private Rectangle _btn;
    public IntPtr Target;

    public RestScreen()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(18, 24, 33);
        Cursor = Cursors.Hand;
        _tick.Tick += (_, _) => Invalidate();
        _tick.Start();
        MouseClick += (_, _) => Back();
        KeyDown += (_, _) => Back();
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var bg = new LinearGradientBrush(r, Color.FromArgb(22, 34, 52), Color.FromArgb(10, 14, 20), 90f))
            g.FillRectangle(bg, r);

        float s = DeviceDpi / 96f;
        int iconSize = (int)(128 * s);
        int cx = r.Width / 2, top = (int)(r.Height * 0.22);
        // נבנה פעם אחת ונשמר. קודם נוצר new Icon(...) בכל ציור (כל שנייה דרך הטיימר), וה-Icon
        // הביניימי לא שוחרר — דליפת GDI handle לשנייה, שאחרי כמה שעות חוצה את תקרת 10,000 וקורסת.
        if (_iconBmp == null)
        {
            using var big = new Icon(_icon, 256, 256);
            _iconBmp = big.ToBitmap();
        }
        g.DrawImage(_iconBmp, cx - iconSize / 2, top, iconSize, iconSize);

        var now = DateTime.Now;
        using var timeF = new Font("Segoe UI Light", 72f, GraphicsUnit.Point);
        using var dateF = new Font("Segoe UI", 20f, GraphicsUnit.Point);
        using var hintF = new Font("Segoe UI", 13f, GraphicsUnit.Point);
        using var dim = new SolidBrush(Color.FromArgb(190, 210, 218, 228));
        var center = new StringFormat { Alignment = StringAlignment.Center };
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center };

        float y = top + iconSize + 24 * s;
        g.DrawString(now.ToString("HH:mm"), timeF, Brushes.White, new RectangleF(0, y, r.Width, 140 * s), center);
        y += 140 * s;
        g.DrawString(Overlay.HebrewDate(now), dateF, Brushes.White, new RectangleF(0, y, r.Width, 44 * s), rtl);
        y += 44 * s;
        g.DrawString(now.ToString("d/M/yyyy"), dateF, dim, new RectangleF(0, y, r.Width, 44 * s), center);
        y += 80 * s;

        // כפתור חזרה
        string label = Lang.T("חזרה לאוצריא", "Back to Otzaria");
        var sz = g.MeasureString(label, dateF);
        _btn = new Rectangle((int)(cx - sz.Width / 2 - 32 * s), (int)y, (int)(sz.Width + 64 * s), (int)(sz.Height + 22 * s));
        using (var path = Overlay.Round(_btn, (int)(_btn.Height / 2f)))
        using (var fill = new SolidBrush(Color.FromArgb(255, 74, 128, 196)))
            g.FillPath(fill, path);
        g.DrawString(label, dateF, Brushes.White, new RectangleF(_btn.X, _btn.Y + 11 * s, _btn.Width, _btn.Height), rtl);
        g.DrawString(Lang.T("לחצו בכל מקום או על מקש כלשהו", "Click anywhere or press any key"), hintF, dim,
            new RectangleF(0, _btn.Bottom + 16 * s, r.Width, 30 * s), rtl);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _icon.Dispose(); _iconBmp?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// אזהרת ספירה-לאחור לפני נעילה מתוזמנת: פס לרוחב המסך בשליש התחתון (לא חלון-דיאלוג מרובע),
/// כדי שיהיה בולט בלי לחסום את כל המסך. מוצגת 5 דקות ודקה לפני תחילת החלון.
/// "אישור" מאשר וסוגר; "ביטול הנעילה" מחליף את תוכן הפס לשדה סיסמה, ובסיסמה נכונה מדלג
/// על הנעילה הקרובה (לא פותחים חלון נפרד — נשאר אותו פס).
/// </summary>
internal sealed class LockCountdown : Form
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly Label _msg = new() { Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White };
    private readonly Label _err = new() { AutoSize = true, ForeColor = Color.FromArgb(255, 138, 128) };
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 220 };
    private readonly Button _ok, _cancel, _confirm, _back;
    private readonly FlowLayoutPanel _normalRow, _pwRow;
    private DateTime _target;
    private bool _pwMode;

    public LockCountdown()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true;
        BackColor = Color.FromArgb(24, 28, 34);
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        Font = new Font("Segoe UI", 11f);

        _msg.Font = new Font("Segoe UI Semibold", 15f);
        _pw.Font = new Font("Segoe UI", 11f);

        _ok = Btn(Lang.T("אישור", "OK"), primary: true);
        _cancel = Btn(Lang.T("ביטול הנעילה", "Cancel lock"), primary: false);
        _confirm = Btn(Lang.T("בטל נעילה", "Unlock"), primary: true);
        _back = Btn(Lang.T("חזרה", "Back"), primary: false);

        _normalRow = Row(_ok, _cancel);
        _pwRow = Row(_pw, _confirm, _back);
        _pwRow.Controls.Add(_err);

        Controls.Add(_msg);
        Controls.Add(_normalRow);
        Controls.Add(_pwRow);

        _ok.Click += (_, _) => Hide();
        _cancel.Click += (_, _) => PwMode(true);
        _back.Click += (_, _) => PwMode(false);
        _confirm.Click += (_, _) => DoCancel();
        _pw.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoCancel(); } };
        _tick.Tick += (_, _) => Upd();
    }

    protected override bool ShowWithoutActivation => true; // לא גונב פוקוס עם ההופעה (כמו שאר ה-overlays)

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */; return cp; }
    }

    private int ScaleX(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private Button Btn(string text, bool primary) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Color.FromArgb(74, 128, 196) : Color.FromArgb(48, 53, 61),
        ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10.5f),
        Padding = new Padding(ScaleX(10), ScaleX(6), ScaleX(10), ScaleX(6)),
        FlatAppearance = { BorderSize = 0 },
    };

    /// <summary>שורת כפתורים/שדות ממורכזת אופקית (לא Dock — ממוקמת ידנית ב-CenterRows כדי לא להימתח לרוחב מלא)</summary>
    private FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = Lang.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft,
        };
        p.Controls.AddRange(controls);
        return p;
    }

    public void SetTarget(DateTime target)
    {
        _target = target;
        Place();
        PwMode(false);
        if (!_tick.Enabled) _tick.Start();
    }

    /// <summary>פס לרוחב מלא של המסך, ממורכז אנכית בתוך השליש התחתון</summary>
    private void Place()
    {
        var b = Screen.PrimaryScreen!.Bounds;
        Width = b.Width;
        Height = ScaleX(150);
        _msg.Height = ScaleX(46);
        int thirdTop = b.Top + b.Height * 2 / 3;
        int thirdHeight = b.Bottom - thirdTop;
        Location = new Point(b.Left, thirdTop + Math.Max(0, (thirdHeight - Height) / 2));
        CenterRows();
    }

    private void CenterRows()
    {
        int y = _msg.Bottom + ScaleX(14);
        _normalRow.Location = new Point((Width - _normalRow.Width) / 2, y);
        _pwRow.Location = new Point((Width - _pwRow.Width) / 2, y);
    }

    private void PwMode(bool on)
    {
        _pwMode = on;
        _normalRow.Visible = !on;
        _pwRow.Visible = on;
        _err.Text = "";
        if (on)
        {
            _msg.Text = Lang.T("הקלידו את סיסמת הנעילה לביטול", "Enter the lock password to cancel");
            _pw.Text = ""; _pw.Focus();
        }
        else Upd(); // מחזיר את טקסט הספירה הרגיל
        CenterRows();
    }

    private void Upd()
    {
        var left = _target - DateTime.Now;
        if (left <= TimeSpan.Zero) { _tick.Stop(); Hide(); return; }
        if (_pwMode) return; // לא דורסים את הנחיית הסיסמה בכל שנייה
        int m = (int)left.TotalMinutes, s = left.Seconds;
        _msg.Text = Lang.T($"המחשב ננעל בעוד {m}:{s:00} לאוצריא בלבד", $"The computer locks to Otzaria only in {m}:{s:00}");
    }

    private void DoCancel()
    {
        try { Kiosk.ScheduleSkipCurrent(_pw.Text); }
        catch (BridgeException ex) { _err.Text = ex.Message; _pw.SelectAll(); _pw.Focus(); CenterRows(); return; }
        catch (Exception ex) { _err.Text = ex.Message; CenterRows(); return; }
        _tick.Stop();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
