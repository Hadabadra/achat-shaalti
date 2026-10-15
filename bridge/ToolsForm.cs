using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OtzariaBridge;

/// <summary>משוב 15.10: טאב "כלים" בחלון הראשי — מה שהיה פריטים נפרדים במגש (צילום מסך, תוספים, שפה)</summary>
internal sealed class ToolsForm : Form
{
    public static void Open() => Hub.Show("tools", () => new ToolsForm());

    private ToolsForm()
    {
        Text = Lang.T("כלים", "Tools");
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoScroll = true };
        Button B(string he, string en, Action a)
        {
            var b = new Button { Text = Lang.T(he, en), AutoSize = true, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 0, 0, 8), MinimumSize = new Size(300, 0) };
            b.Click += (_, _) => { try { a(); } catch (Exception ex) { MessageBox.Show(ex.Message, Program.AppName); } };
            flow.Controls.Add(b); return b;
        }
        B("צילום מסך (Ctrl+Shift+F11)", "Screenshot (Ctrl+Shift+F11)", () => Shot(false));
        B("צילום אזור (Ctrl+Shift+F12)", "Region screenshot (Ctrl+Shift+F12)", () => Shot(true));
        B("התקן/עדכן את התוספים באוצריא", "Install/update the plugins in Otzaria", () => { Installer.DeployPlugins(); Installer.InstallPluginsInOtzaria(); });
        B("פתח את תיקיית התוספים", "Open the plugins folder", Installer.OpenPluginsFolder);
        B("English", "עברית", () => { Lang.Set(Lang.En ? "he" : "en"); MessageBox.Show(Lang.T("השפה תתעדכן בפתיחה הבאה של החלון.", "The language will update next time the window opens."), Program.AppName); });
        Controls.Add(flow);
    }

    /// <summary>מסתירים את החלון לרגע כדי שלא ייכנס לצילום</summary>
    private void Shot(bool region)
    {
        var top = TopLevelControl as Form;
        top?.Hide();
        Task.Run(() =>
        {
            Thread.Sleep(300);
            try { Tray.Invoke(() => { try { Screenshot.Capture(region); } finally { top?.Show(); } }); }
            catch { Tray.Invoke(() => top?.Show()); }
        });
    }
}
