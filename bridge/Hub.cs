using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace OtzariaBridge;

/// <summary>C1: חלון אחד עם טאבים לכל חלונות המגש (אודות, שינויים, דיווחים, צימוד).
/// כל טופס קיים מוטמע כטאב (TopLevel=false) במקום לפתוח חלון נפרד.</summary>
internal sealed class Hub : Form
{
    private static Hub _inst;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, TabPage> _pages = new();

    public static void Show(string key, Func<Form> make)
    {
        if (_inst == null || _inst.IsDisposed) _inst = new Hub();
        _inst.Open(key, make);
    }

    private Hub()
    {
        Text = Program.AppName;
        try { Icon = Overlay.AppIcon(); } catch { }
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        Size = new Size(760, 620);
        MinimumSize = new Size(520, 420);
        Controls.Add(_tabs);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab != null) Text = Program.AppName + " — " + _tabs.SelectedTab.Text; };
    }

    private void Open(string key, Func<Form> make)
    {
        if (!_pages.TryGetValue(key, out var page) || page.IsDisposed || page.Controls.Count == 0)
        {
            var f = make();
            page = new TabPage(f.Text) { UseVisualStyleBackColor = true };
            f.TopLevel = false;
            f.FormBorderStyle = FormBorderStyle.None;
            f.Dock = DockStyle.Fill;
            page.Controls.Add(f);
            f.FormClosed += (_, _) => { _tabs.TabPages.Remove(page); _pages.Remove(key); if (_tabs.TabCount == 0) Close(); };
            _pages[key] = page;
            _tabs.TabPages.Add(page);
            f.Show();
        }
        _tabs.SelectedTab = page;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
}
