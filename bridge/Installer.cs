using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>התקנה עצמית ל-%LOCALAPPDATA%\OtzariaBridge, הפעלה אוטומטית, קיצור דרך ופריסת התוספים</summary>
public static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OtzariaBridge";
    private const string ShortcutName = "אחת שאלתי.lnk";
    private static readonly string[] OldShortcutNames = { "גשר אוצריא.lnk", "תורת אסתר.lnk", "תוכנת חסימת אוצריא.lnk" }; // שמות קודמים, עד 3.0.0 ועד שינוי השם

    public static string InstalledExe => Path.Combine(Config.Dir, "OtzariaBridge.exe");
    public static string PluginsDir => Path.Combine(Config.Dir, "plugins");
    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public static bool IsRunningFromInstallDir() =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool HasAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null;
    }

    public static void Install(bool showMessage)
    {
        Directory.CreateDirectory(Config.Dir);

        if (!IsRunningFromInstallDir())
        {
            StopOtherInstances();
            File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
        }

        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            k.SetValue(RunValue, "\"" + InstalledExe + "\" --background");

        foreach (string old in OldShortcutNames)
            try { string p = Path.Combine(Path.GetDirectoryName(ShortcutPath)!, old); if (File.Exists(p)) File.Delete(p); } catch { }
        try { CreateShortcut(); }
        catch (Exception ex) { Config.Log("shortcut: " + ex.Message); }

        int plugins = DeployPlugins();
        bool canInstall = plugins > 0 && OtzariaProtocolRegistered();

        Process.Start(new ProcessStartInfo(InstalledExe, "--background") { UseShellExecute = false, WorkingDirectory = Config.Dir });

        if (showMessage)
        {
            string msg = Lang.T("תורת אסתר (גשר אוצריא) הותקן ויעלה אוטומטית עם כניסה ל-Windows.",
                "Otzaria Bridge is installed and will start automatically when you sign in to Windows.");
            if (canInstall)
                msg += Lang.T("\n\nעכשיו אוצריא תיפתח ותציע להתקין את התוספים \"סייר קבצים\" ו\"הגדרות מערכת\" — אשרו בחלון ההתקנה של אוצריא.",
                       "\n\nOtzaria will now open and offer to install the \"File Explorer\" and \"System Settings\" plugins — confirm in Otzaria's install window.");
            else if (plugins > 0)
                msg += Lang.T("\n\nבתיקייה שנפתחה נמצאים התוספים \"סייר קבצים\" ו\"הגדרות מערכת\". " +
                       "יש להתקין אותם מתוך אוצריא (התקנת תוסף מקובץ).",
                       "\n\nThe folder that opened contains the \"File Explorer\" and \"System Settings\" plugins. " +
                       "Install them from inside Otzaria (install plugin from file).");
            if (Lang.En)
                MessageBox.Show(msg, Program.AppNameEn, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(msg, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (!canInstall && plugins > 0) OpenPluginsFolder();
        }

        // גם בהתקנה שקטה (מתקין Inno): מבקשים מאוצריא להתקין את התוספים, ומחכים עד שכולם נשלחו —
        // אחרת התהליך יוצא והחוט ברקע נהרג לפני התוסף השני
        if (canInstall) InstallPluginsInOtzaria(wait: true);
    }

    /// <summary>האם אוצריא רשמה את סכמת otzaria:// (היא עושה זאת בעלייה הראשונה שלה)</summary>
    private static bool OtzariaProtocolRegistered()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\otzaria\shell\open\command")
                          ?? Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"otzaria\shell\open\command");
            return k?.GetValue("") is string v && v.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>פותח באוצריא את זרימת ההתקנה של כל תוסף (otzaria://plugin/install-local), אחד אחרי השני.
    /// אוצריא מציגה את בקשת אישור ההרשאות; המשתמש מאשר בעצמו.</summary>
    public static void InstallPluginsInOtzaria(bool wait = false)
    {
        var files = Directory.Exists(PluginsDir) ? Directory.GetFiles(PluginsDir, "*.otzplugin") : Array.Empty<string>();
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var t = new Thread(() =>
        {
            foreach (var f in files)
            {
                try
                {
                    string uri = "otzaria://plugin/install-local?path=" + Uri.EscapeDataString(Path.GetFullPath(f));
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                }
                catch (Exception ex) { Config.Log("install-local: " + ex.Message); }
                Thread.Sleep(6000);
            }
        }) { IsBackground = true };
        t.Start();
        if (wait) t.Join();
    }

    /// <summary>פורס את קובצי ה-.otzplugin המוטמעים לתיקיית plugins</summary>
    public static int DeployPlugins()
    {
        int count = 0;
        var asm = Assembly.GetExecutingAssembly();
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".otzplugin", StringComparison.OrdinalIgnoreCase)))
        {
            Directory.CreateDirectory(PluginsDir);
            string name = res.StartsWith("plugins.") ? res["plugins.".Length..] : res;
            using var s = asm.GetManifestResourceStream(res)!;
            using var f = File.Create(Path.Combine(PluginsDir, name));
            s.CopyTo(f);
            count++;
        }
        return count;
    }

    public static void OpenPluginsFolder()
    {
        if (DeployPlugins() == 0 && !Directory.Exists(PluginsDir)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PluginsDir + "\"") { UseShellExecute = true });
    }

    private static void CreateShortcut()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = InstalledExe;
            lnk.Arguments = "--background";
            lnk.WorkingDirectory = Config.Dir;
            lnk.Description = Program.AppFullName;
            lnk.IconLocation = InstalledExe + ",0";
            lnk.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static void StopOtherInstances()
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("OtzariaBridge"))
        {
            if (p.Id == self) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    /// <summary>מחזיר הודעת שגיאה, או null בהצלחה</summary>
    public static string Uninstall(string password)
    {
        // רכיב ההקשחה (אם מותקן) מוסר קודם, עם אותה סיסמה
        string herr = HardLock.UninstallWithPassword(password);
        if (herr != null) return herr;
        string err = Kiosk.ReleaseForUninstall(password);
        if (err != null) return err;

        StopOtherInstances();
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k?.DeleteValue(RunValue, false);
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }

        if (IsRunningFromInstallDir())
        {
            // אי אפשר למחוק קובץ שרץ; מוחקים אחרי היציאה
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Config.Dir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        else
        {
            try { Directory.Delete(Config.Dir, true); } catch (Exception ex) { return "התיקייה לא נמחקה: " + ex.Message; }
        }
        return null;
    }
}
