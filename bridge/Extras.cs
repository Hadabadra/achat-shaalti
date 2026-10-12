using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// סרגל תוכנות בצד המסך בזמן נעילה: אוצריא והתוכנות התורניות המותרות. לחיצה מעבירה לחזית (או פותחת, אם סגורה).
/// מכווץ לפס צר ומתרחב כשהעכבר מתקרב, כדי לא להסתיר את אוצריא.
/// </summary>
internal sealed class SideBar : Form
{
    private sealed class Item { public string Name; public bool Running; public Programs.Entry Entry; public Bitmap Icon; }
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 150 };
    private List<Item> _items = new();
    private bool _expanded;
    private int _hover = -1;
    private DateTime _lastScan = DateTime.MinValue;
    private int _scanBusy;

    public SideBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 30, 40);
        Opacity = 0.94;
        RightToLeft = RightToLeft.Yes;
        _tick.Tick += (_, _) => Step();
        MouseMove += (_, e) => { _hover = HitTest(e.Location); Invalidate(); };
        MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
        MouseClick += (_, e) => Click_(HitTest(e.Location));
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
            return cp;
        }
    }

    private float S => DeviceDpi / 96f;
    private int RowH => (int)(46 * S);
    private bool IconsOnly => Config.Get("kiosk.sideBarIconsOnly", false);
    private int FullW => (int)((IconsOnly ? 60 : 196) * S);
    private int StripW => (int)(12 * S);

    public void Rebuild()
    {
        var list = new List<Item> { new Item { Name = Lang.T("אוצריא", "Otzaria"), Running = true, Icon = IconOf(Kiosk.OtzariaPath()) } };
        try { foreach (var e in Programs.Load()) list.Add(new Item { Name = e.Name, Entry = e, Icon = IconOf(e.Path) }); } catch { }
        foreach (var old in _items) old.Icon?.Dispose();
        _items = list;
        if (_items.Count <= 1) { Hide(); return; }   // אין תוכנות מותרות — אין מה להציג
        Place();
        if (!Visible) Show();
        _tick.Start();
        Scan();
    }

    /// <summary>האייקון המקורי של קובץ ההפעלה (מה-exe עצמו); null אם אין קובץ או שהשליפה נכשלה</summary>
    private static Bitmap IconOf(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var ic = Icon.ExtractAssociatedIcon(path);
            return ic?.ToBitmap();
        }
        catch (Exception ex) { Config.Log("sidebar icon: " + ex.Message); return null; }
    }

    private void Place()
    {
        var b = Screen.PrimaryScreen!.Bounds;
        int h = _items.Count * RowH + (int)(16 * S);
        int w = _expanded ? FullW : StripW;
        bool left = Config.Get("kiosk.sideBarPosition", "right") == "left";
        int x = left ? b.Left : b.Right - w;
        int y = b.Top + (b.Height - h) / 2;
        SetBounds(x, y, w, h);
        Region = new Region(Overlay.Round(new Rectangle(0, 0, Width, Height), (int)(10 * S)));
        Invalidate();
    }

    private void Step()
    {
        bool inside = Bounds.Contains(Cursor.Position);
        bool want = inside || (_expanded && Bounds.Contains(Cursor.Position));
        if (want != _expanded) { _expanded = want; Place(); }
        if ((DateTime.UtcNow - _lastScan).TotalSeconds >= 2) Scan();
    }

    private void Scan()
    {
        _lastScan = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _scanBusy, 1) == 1) return;
        var snapshot = _items.ToList();
        Task.Run(() =>
        {
            try { foreach (var it in snapshot.Where(i => i.Entry != null)) it.Running = Programs.ExistingWindow(it.Entry) != IntPtr.Zero; }
            catch { }
            finally { Interlocked.Exchange(ref _scanBusy, 0); }
            try { BeginInvoke(new Action(Invalidate)); } catch { }
        });
    }

    private int HitTest(Point p)
    {
        int i = (p.Y - (int)(8 * S)) / RowH;
        return _expanded && i >= 0 && i < _items.Count ? i : -1;
    }

    private void Click_(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        var it = _items[i];
        Task.Run(() =>
        {
            try
            {
                if (it.Entry == null) Overlay.BringOtzaria(IntPtr.Zero);
                else Programs.Activate(it.Entry);
            }
            catch (Exception ex) { Config.Log("sidebar: " + ex.Message); }
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var r = ClientRectangle;
        if (!_expanded)
        {
            using var grip = new SolidBrush(Color.FromArgb(120, 150, 190));
            int gw = (int)(4 * S), gh = (int)(36 * S);
            g.FillRectangle(grip, (r.Width - gw) / 2, (r.Height - gh) / 2, gw, gh);
            return;
        }
        using var f = new Font("Segoe UI", 11f, GraphicsUnit.Point);
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        for (int i = 0; i < _items.Count; i++)
        {
            var row = new Rectangle((int)(6 * S), (int)(8 * S) + i * RowH, r.Width - (int)(12 * S), RowH - (int)(4 * S));
            if (i == _hover)
                using (var hb = new SolidBrush(Color.FromArgb(70, 100, 150, 220))) g.FillRectangle(hb, row);
            var it = _items[i];
            int isz = (int)(30 * S);
            // ימין לשמאל: האייקון בצד הימני של השורה, השם משמאלו
            var ir = IconsOnly
                ? new Rectangle(row.Left + (row.Width - isz) / 2, row.Top + (row.Height - isz) / 2, isz, isz)
                : new Rectangle(row.Right - isz - (int)(8 * S), row.Top + (row.Height - isz) / 2, isz, isz);
            if (it.Icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(it.Icon, ir);
            }
            else
            {
                // אין אייקון: עיגול עם האות הראשונה
                using var ib = new SolidBrush(Color.FromArgb(70, 90, 120));
                g.FillEllipse(ib, ir);
                using var lf = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
                var cf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(string.IsNullOrEmpty(it.Name) ? "?" : it.Name.Substring(0, 1), lf, Brushes.White, ir, cf);
            }
            // נקודת "פתוח" קטנה בפינת האייקון
            int ds = (int)(10 * S);
            var dot = new Rectangle(ir.Left - ds / 3, ir.Bottom - ds + ds / 3, ds, ds);
            using (var ring = new SolidBrush(BackColor)) g.FillEllipse(ring, Rectangle.Inflate(dot, (int)(2 * S), (int)(2 * S)));
            using (var db = new SolidBrush(it.Running ? Color.FromArgb(80, 200, 120) : Color.FromArgb(110, 120, 135))) g.FillEllipse(db, dot);
            if (!IconsOnly)
            {
                var tr = new RectangleF(row.Left + 6 * S, row.Top, ir.Left - row.Left - 14 * S, row.Height);
                g.DrawString(it.Name, f, Brushes.White, tr, rtl);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); foreach (var it in _items) it.Icon?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>חלון יומן שינויים מקומי (CHANGELOG מוטמע ב-exe; לא צריך רשת) — כל הגרסאות</summary>
internal sealed class ChangelogForm : Form
{
    private static ChangelogForm _open;

    public static void Open()
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        _open = new ChangelogForm();
        _open.Show();
    }

    public static string LoadText()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (s == null) return Lang.T("יומן השינויים לא נמצא בקובץ.", "Changelog not found in this build.");
            using var rd = new StreamReader(s, Encoding.UTF8);
            return rd.ReadToEnd();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private ChangelogForm()
    {
        Text = Lang.T("יומן שינויים — " + Program.AppName, "Changelog — " + Program.AppNameEn);
        Icon = Overlay.AppIcon();
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(760, 600);

        string text = LoadText();
        var versions = Regex.Matches(text, @"^## (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
        var box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.None, RightToLeft = RightToLeft.Yes, Font = new Font("Segoe UI", 10.5f) };
        var pick = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        pick.Items.Add(Lang.T("קפיצה לגרסה…", "Jump to version…"));
        foreach (var v in versions) pick.Items.Add(v);
        pick.SelectedIndex = 0;
        Controls.Add(box);
        Controls.Add(pick);

        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw;
            Font f = box.Font; Color c = Color.Black;
            if (line.StartsWith("## ")) { line = line[3..]; f = new Font("Segoe UI Semibold", 14f); c = Color.FromArgb(30, 80, 160); }
            else if (line.StartsWith("### ")) { line = line[4..]; f = new Font("Segoe UI Semibold", 11.5f); }
            else if (line.StartsWith("# ")) { line = line[2..]; f = new Font("Segoe UI Semibold", 17f); }
            else if (line.TrimStart().StartsWith("- ")) line = "• " + line.TrimStart()[2..];
            else if (line.Trim() == "---") continue;
            line = line.Replace("**", "").Replace("`", "");
            box.SelectionFont = f; box.SelectionColor = c;
            box.AppendText(line + "\n");
        }
        pick.SelectedIndexChanged += (_, _) =>
        {
            if (pick.SelectedIndex <= 0) return;
            int i = box.Text.IndexOf(versions[pick.SelectedIndex - 1].Replace("**", "").Replace("`", ""), StringComparison.Ordinal);
            if (i >= 0) { box.SelectionStart = i; box.SelectionLength = 0; box.ScrollToCaret(); }
        };
        box.SelectionStart = 0; box.ScrollToCaret();
    }
}

/// <summary>חלון קוד שחזור (Ctrl+Alt+Shift+F12) לביטול חירום של הנעילה</summary>
internal sealed class EmergencyForm : Form
{
    private static EmergencyForm _open;

    public static void Open()
    {
        if (!Kiosk.IsActive) return;
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        _open = new EmergencyForm();
        _open.Show();
        Kiosk.BringToFront(_open.Handle);
    }

    private EmergencyForm()
    {
        Text = Lang.T("ביטול חירום של הנעילה", "Emergency unlock");
        Icon = Overlay.AppIcon();
        TopMost = true;
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10.5f);
        ClientSize = new Size(420, 190);
        var info = new Label { Text = Lang.T("הזינו את קוד השחזור (12 תווים) כדי לבטל את הנעילה כולה.", "Enter the 12-character recovery code to turn off lock mode completely."), Dock = DockStyle.Top, Height = 56, Padding = new Padding(12, 12, 12, 0) };
        var code = new TextBox { Dock = DockStyle.Top, Font = new Font("Consolas", 14f), TextAlign = HorizontalAlignment.Center, CharacterCasing = CharacterCasing.Upper, RightToLeft = RightToLeft.No };
        var msg = new Label { Dock = DockStyle.Top, Height = 28, ForeColor = Color.Firebrick, TextAlign = ContentAlignment.MiddleCenter };
        var ok = new Button { Text = Lang.T("בטל נעילה", "Unlock"), Dock = DockStyle.Bottom, Height = 40 };
        Controls.AddRange(new Control[] { ok, msg, code, info });
        AcceptButton = ok;
        ok.Click += (_, _) =>
        {
            if (Kiosk.EmergencyRelease(code.Text)) { Close(); return; }
            msg.Text = Lang.T("הקוד שגוי", "Wrong code");
            code.SelectAll();
        };
        Shown += (_, _) => code.Focus();
    }
}

/// <summary>
/// איסוף יומנים לדיווח באג. שום דבר לא נשלח אוטומטית: נוצר קובץ zip בשולחן העבודה (בלי סיסמאות ומפתחות),
/// ונפתח בדפדפן דף "בעיה חדשה" ב-GitHub עם פרטים בסיסיים. המשתמשת מצרפת את הקובץ בגרירה ושולחת.
/// </summary>
internal static class Diagnostics_
{
    public const string IssuesUrl = "https://github.com/e0548433917-gif/otzaria-system-tools/issues/new";
    private static readonly Regex Secret = new("(hash|salt|key|secret|password|token|recovery)", RegexOptions.IgnoreCase);

    public static void Report()
    {
        if (Kiosk.IsActive)
        {
            MessageBox.Show(Lang.T("בזמן נעילה אי אפשר לפתוח דיווח (הוא פותח דפדפן). בטלו את הנעילה ונסו שוב.", "Reporting is unavailable while lock mode is on (it opens a browser). Turn off lock mode and try again."),
                Lang.T("דיווח באג", "Report a bug"));
            return;
        }
        string consent = Lang.T(
            "ייווצר קובץ zip בשולחן העבודה עם יומן הגשר והגדרות (בלי סיסמאות, קודים ומפתחות), ויפתח דף דיווח ב-GitHub.\nשום דבר לא נשלח אוטומטית: אתם מצרפים את הקובץ ושולחים בעצמכם.\n\nלהמשיך?",
            "A zip with the bridge log and settings (no passwords, codes or keys) will be created on your desktop, and a GitHub issue page will open.\nNothing is sent automatically: you attach the file and submit it yourself.\n\nContinue?");
        if (MessageBox.Show(consent, Lang.T("דיווח באג", "Report a bug"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1, Lang.En ? 0 : MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
        try
        {
            string zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "otzaria-bridge-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                string log = Path.Combine(Config.Dir, "bridge.log");
                if (File.Exists(log))
                {
                    using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    long keep = 600_000; if (fs.Length > keep) fs.Seek(-keep, SeekOrigin.End);
                    using var entry = z.CreateEntry("bridge.log").Open();
                    fs.CopyTo(entry);
                }
                AddText(z, "config.sanitized.json", Sanitize(File.Exists(Config.FilePath) ? File.ReadAllText(Config.FilePath) : "{}"));
                AddText(z, "info.txt", Info());
            }
            Process.Start(new ProcessStartInfo(IssuesUrl + "?title=" + Uri.EscapeDataString("דיווח מהגשר " + Program.Version) +
                "&body=" + Uri.EscapeDataString("**מה קרה?**\n\n\n**מה ציפיתי שיקרה?**\n\n\n(צרפו כאן את קובץ ה-zip מהשולחן העבודה בגרירה)\n\n" + Info())) { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + zip + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private static void AddText(ZipArchive z, string name, string text)
    {
        using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string Info() =>
        "גרסת גשר: " + Program.Version + "\nמערכת: " + Environment.OSVersion + "\nנעילה פעילה: " + Kiosk.IsActive +
        "\nשפת תצוגה: " + (Lang.En ? "en" : "he") + "\n";

    /// <summary>מסיר כל ערך שהמפתח שלו נראה כמו סוד</summary>
    private static string Sanitize(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            Strip(node);
            return node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "{}";
        }
        catch { return "{}"; }
    }

    private static void Strip(System.Text.Json.Nodes.JsonNode n)
    {
        if (n is System.Text.Json.Nodes.JsonObject o)
        {
            foreach (var k in o.Select(x => x.Key).ToList())
            {
                if (Secret.IsMatch(k)) o[k] = "***"; else Strip(o[k]);
            }
        }
        else if (n is System.Text.Json.Nodes.JsonArray a) foreach (var x in a) Strip(x);
    }
}
