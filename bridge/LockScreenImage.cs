using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OtzariaBridge;

/// <summary>
/// משוב 15.10: תמונת מסך הנעילה של Windows = מסך המנוחה (פסוק, סמל, זמני היום, הקדשה).
/// kiosk.lockScreenImage (ברירת מחדל כבוי). מתעדכן כל 30 דקות (הזמנים משתנים) דרך
/// Windows.System.UserProfile.LockScreen.SetImageFileAsync — עובד למשתמש רגיל ב-Windows 10/11, "זרקור Windows" מכובה אוטומטית (HKCU).
/// שם קובץ מתחלף (a/b) כי Windows שומר במטמון לפי שם.
/// </summary>
internal static class LockScreenImage
{
    private static System.Threading.Timer _timer;
    private static bool _flip;

    public static void Start() => _timer ??= new System.Threading.Timer(_ => Tray.Invoke(Update), null, 20_000, 30 * 60_000);

    /// <summary>"זרקור Windows" מחליף את תמונת מסך הנעילה — מכבים אותו למשתמש הנוכחי (HKCU, בלי הרשאות מנהל)</summary>
    private static void DisableSpotlight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
            k.SetValue("RotatingLockScreenEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("RotatingLockScreenOverlayEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) { Config.Log("lockscreen spotlight: " + ex.Message); }
    }

    public static void Update()
    {
        if (!Config.Get("kiosk.lockScreenImage", true)) return;
        try
        {
            var scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            int w = Math.Max(1280, scr.Width), h = Math.Max(720, scr.Height);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge");
            Directory.CreateDirectory(dir);
            _flip = !_flip;
            string path = Path.Combine(dir, "lockscreen-" + (_flip ? "a" : "b") + ".png");
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            using (var rs = new RestScreen { StillImage = true })
            {
                rs.Render(g, new Rectangle(0, 0, w, h), 0f, h / 1080f * 1.25f);
                bmp.Save(path, ImageFormat.Png);
            }
            DisableSpotlight();
            Task.Run(async () =>
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
                }
                catch (Exception ex) { Config.Log("lockscreen: " + ex.Message); }
            });
        }
        catch (Exception ex) { Config.Log("lockscreen render: " + ex.Message); }
    }
}
