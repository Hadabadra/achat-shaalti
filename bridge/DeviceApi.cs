using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;

namespace OtzariaBridge;

/// <summary>נקודות הקצה /sys/usb, /sys/keyboard ו-/sys/energy: הוצאת USB, מקלדת, תאורת מקלדת וחיסכון בחשמל.
/// מה שלא נתמך במחשב מחזיר unsupported או supported=false</summary>
public static class DeviceApi
{
    public static void Register()
    {
        Server.Route("/sys/usb/list", _ => new { drives = Usb.List() });
        Server.Route("/sys/usb/eject", b => Usb.Eject(b.Req("letter")));
        Server.Route("/sys/keyboard/get", _ => Keyboard.Get());
        Server.Route("/sys/keyboard/set", Keyboard.Set);
        Server.Route("/sys/keyboard/backlight/get", Keyboard.BacklightGet);
        Server.Route("/sys/keyboard/backlight/set", Keyboard.BacklightSet);
        Server.Route("/sys/energy/get", _ => Energy.Get());
        Server.Route("/sys/energy/set", Energy.Set);
    }
}

// ---------- USB ----------

/// <summary>כונני USB מחוברים (גם דיסקים חיצוניים) והוצאה בטוחה: נעילה, ניתוק וגריסה של הכרך</summary>
public record UsbDrive(string Letter, string Label, string Model, string Fs, long SizeBytes, long FreeBytes, bool Ready);

public static class Usb
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle h);

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 3, OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018, FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804, IoctlStorageEjectMedia = 0x002D4808;

    private static string Norm(string letter)
    {
        string s = (letter ?? "").Trim().TrimEnd('\\', ':').ToUpperInvariant();
        if (s.Length != 1 || s[0] < 'A' || s[0] > 'Z') throw BridgeException.BadRequest("אות כונן לא תקינה");
        return s + ":";
    }

    public static List<UsbDrive> List()
    {
        var res = new List<UsbDrive>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var disks = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive WHERE InterfaceType='USB'");
            foreach (ManagementObject d in disks.Get())
            {
                string devId = ((d["DeviceID"] as string) ?? "").Replace("\\", "\\\\");
                string model = (d["Model"] as string) ?? "";
                using var parts = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + devId + "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject p in parts.Get())
                {
                    string pid = ((p["DeviceID"] as string) ?? "").Replace("\\", "\\\\");
                    using var lds = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + pid + "'} WHERE AssocClass=Win32_LogicalDiskToPartition");
                    foreach (ManagementObject ld in lds.Get())
                    {
                        string letter = ld["DeviceID"] as string;
                        if (string.IsNullOrEmpty(letter) || !seen.Add(letter)) continue;
                        res.Add(Describe(letter, model));
                    }
                }
            }
        }
        catch (Exception ex) { Config.Log("usb list: " + ex.Message); }

        // גיבוי: כוננים נתיקים שלא זוהו דרך WMI
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.Removable || !di.IsReady) continue;
                string letter = di.Name.Substring(0, 2).ToUpperInvariant();
                if (seen.Add(letter)) res.Add(Describe(letter, ""));
            }
        }
        catch { }
        return res;
    }

    private static UsbDrive Describe(string letter, string model)
    {
        string label = "", fs = "";
        long size = 0, free = 0;
        bool ready = false;
        try
        {
            var di = new DriveInfo(letter);
            if (di.IsReady) { ready = true; label = di.VolumeLabel; fs = di.DriveFormat; size = di.TotalSize; free = di.AvailableFreeSpace; }
        }
        catch { }
        return new UsbDrive(letter, label, model, fs, size, free, ready);
    }

    public static object Eject(string letterArg)
    {
        string letter = Norm(letterArg);
        string sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Substring(0, 2).ToUpperInvariant();
        if (letter == sysDrive) throw BridgeException.Forbidden("זה כונן המערכת, אי אפשר להוציא אותו");
        if (!List().Any(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase)))
            throw BridgeException.NotFound("הכונן " + letter + " אינו התקן USB מחובר");

        using var h = CreateFile("\\\\.\\" + letter, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 5) throw BridgeException.Forbidden("להוצאת כונן זה נדרשות הרשאות מנהל. אפשר להשתמש בהוצאה בטוחה של Windows בסרגל המשימות.");
            throw new BridgeException("busy", "לא ניתן לגשת לכונן " + letter + " (שגיאה " + err + ")", 409);
        }

        FlushFileBuffers(h);
        bool locked = false;
        for (int i = 0; i < 4 && !locked; i++)
        {
            locked = DeviceIoControl(h, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!locked) Thread.Sleep(500);
        }
        if (!locked)
            throw new BridgeException("busy", "הכונן בשימוש. סגרו קבצים ותוכנות שפתוחים ממנו (כולל חלונות סייר) ונסו שוב.", 409);

        if (!DeviceIoControl(h, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new BridgeException("busy", "ניתוק הכרך נכשל. נסו שוב בעוד רגע.", 409);

        // מתיר הוצאה ואז מוציא; בהתקנים שלא תומכים בכך הכרך כבר מנותק ובטוח לשליפה
        bool ejected = false;
        IntPtr buf = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buf, 0);
            DeviceIoControl(h, IoctlStorageMediaRemoval, buf, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
            ejected = DeviceIoControl(h, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(buf); }
        Config.Log("usb eject " + letter + " ejected=" + ejected);
        return new { ok = true, letter, ejected };
    }
}

// ---------- מקלדת ----------

/// <summary>פריסות, קצב הקשה, מקשים דביקים וסינון, Caps/Num Lock, ותאורת מקלדת (LampArray)</summary>
public static class Keyboard
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")]
    private static extern bool Spi(uint action, uint param, uint[] buf, uint ini);

    private const uint SpiGetDelay = 0x0016, SpiSetDelay = 0x0017, SpiGetSpeed = 0x000A, SpiSetSpeed = 0x000B;
    private const uint SpiGetSticky = 0x003A, SpiSetSticky = 0x003B;
    private const uint SpiGetFilter = 0x0032, SpiSetFilter = 0x0033;
    private const uint SpiGetToggle = 0x0034, SpiSetToggle = 0x0035;
    private const uint Persist = 0x01 | 0x02; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
    private const uint StickyOn = 0x1, StickyHotkey = 0x4, StickyConfirm = 0x8;

    private static string Hkl(IntPtr h) => ((uint)h.ToInt64()).ToString("X8");

    private static uint[] Read(uint action, int words)
    {
        var buf = new uint[words];
        buf[0] = (uint)(words * 4);
        Spi(action, (uint)(words * 4), buf, 0);
        return buf;
    }

    private static void Write(uint action, uint[] buf) => Spi(action, (uint)(buf.Length * 4), buf, Persist);

    public static object Get()
    {
        var delay = new uint[1]; var speed = new uint[1];
        Spi(SpiGetDelay, 0, delay, 0);
        Spi(SpiGetSpeed, 0, speed, 0);
        var sk = Read(SpiGetSticky, 2);
        var fk = Read(SpiGetFilter, 6);
        var tk = Read(SpiGetToggle, 2);

        uint tid = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        string current = Hkl(GetKeyboardLayout(tid));
        var layouts = new List<object>();
        foreach (InputLanguage l in InputLanguage.InstalledInputLanguages)
            layouts.Add(new { id = Hkl(l.Handle), name = l.Culture.NativeName + " — " + l.LayoutName, lang = l.Culture.TwoLetterISOLanguageName });

        return new
        {
            layouts,
            current,
            delay = (int)delay[0],
            speed = (int)speed[0],
            stickyKeys = (sk[1] & StickyOn) != 0,
            stickyHotkey = (sk[1] & StickyHotkey) != 0,
            filterKeys = (fk[1] & 1) != 0,
            toggleKeys = (tk[1] & 1) != 0,
            capsLock = (GetKeyState(0x14) & 1) != 0,
            numLock = (GetKeyState(0x90) & 1) != 0,
        };
    }

    private static void SetLock(int vk, bool want)
    {
        if (((GetKeyState(vk) & 1) != 0) == want) return;
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        keybd_event((byte)vk, 0, 2, UIntPtr.Zero);
    }

    public static object Set(JsonElement b)
    {
        double? d = b.Num("delay");
        if (d.HasValue) Spi(SpiSetDelay, (uint)Math.Clamp(Math.Round(d.Value), 0, 3), null, Persist);
        double? s = b.Num("speed");
        if (s.HasValue) Spi(SpiSetSpeed, (uint)Math.Clamp(Math.Round(s.Value), 0, 31), null, Persist);

        bool? sticky = b.BoolOpt("stickyKeys"), hotkey = b.BoolOpt("stickyHotkey");
        if (sticky.HasValue || hotkey.HasValue)
        {
            var sk = Read(SpiGetSticky, 2);
            if (sticky.HasValue) sk[1] = sticky.Value ? sk[1] | StickyOn : sk[1] & ~StickyOn;
            if (hotkey.HasValue) sk[1] = hotkey.Value ? sk[1] | StickyHotkey | StickyConfirm : sk[1] & ~(StickyHotkey | StickyConfirm);
            Write(SpiSetSticky, sk);
        }
        bool? filter = b.BoolOpt("filterKeys");
        if (filter.HasValue)
        {
            var fk = Read(SpiGetFilter, 6);
            fk[1] = filter.Value ? fk[1] | 1u : fk[1] & ~1u;
            Write(SpiSetFilter, fk);
        }
        bool? toggle = b.BoolOpt("toggleKeys");
        if (toggle.HasValue)
        {
            var tk = Read(SpiGetToggle, 2);
            tk[1] = toggle.Value ? tk[1] | 1u : tk[1] & ~1u;
            Write(SpiSetToggle, tk);
        }
        bool? caps = b.BoolOpt("capsLock"), num = b.BoolOpt("numLock");
        if (caps.HasValue) SetLock(0x14, caps.Value);
        if (num.HasValue) SetLock(0x90, num.Value);

        string layout = b.Str("layout");
        if (!string.IsNullOrEmpty(layout))
        {
            InputLanguage found = null;
            foreach (InputLanguage l in InputLanguage.InstalledInputLanguages)
                if (string.Equals(Hkl(l.Handle), layout, StringComparison.OrdinalIgnoreCase)) { found = l; break; }
            if (found == null) throw BridgeException.NotFound("הפריסה לא מותקנת");
            // הפריסה מוחלפת בחלון שבחזית (אוצריא), כמו קיצור Alt+Shift
            PostMessage(GetForegroundWindow(), 0x0050, IntPtr.Zero, found.Handle);
        }
        return Get();
    }

    // ---------- תאורת מקלדת ----------
    // ל-Windows אין API אחיד לתאורה לבנה רגילה של מחשבים ניידים (זה תלוי ביצרן).
    // נתמכות מקלדות עם LampArray (HID), בעיקר מקלדות RGB. בשאר המחשבים מוחזר supported=false.

    private static async Task<List<(string name, LampArray lamp)>> Lamps()
    {
        var res = new List<(string, LampArray)>();
        var infos = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector());
        foreach (var i in infos)
        {
            try
            {
                var la = await LampArray.FromIdAsync(i.Id);
                if (la != null && la.LampArrayKind == LampArrayKind.Keyboard) res.Add((i.Name, la));
            }
            catch { }
        }
        return res;
    }

    private static (string vendor, string model) Machine()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get())
                return (((o["Manufacturer"] as string) ?? "").Trim(), ((o["Model"] as string) ?? "").Trim());
        }
        catch { }
        return ("", "");
    }

    public static async Task<object> BacklightGet(JsonElement b)
    {
        var (vendor, model) = Machine();
        try
        {
            var l = await Lamps();
            if (l.Count > 0)
                return new
                {
                    supported = true,
                    name = l[0].name,
                    enabled = l[0].lamp.IsEnabled,
                    brightness = (int)Math.Round(l[0].lamp.BrightnessLevel * 100),
                    vendor,
                    model,
                };
        }
        catch (Exception ex) { Config.Log("backlight: " + ex.Message); }
        return new { supported = false, name = "", enabled = false, brightness = 0, vendor, model };
    }

    public static async Task<object> BacklightSet(JsonElement b)
    {
        var l = await Lamps();
        if (l.Count == 0) throw BridgeException.Unsupported("תאורת המקלדת אינה נתמכת במחשב זה");
        bool? enabled = b.BoolOpt("enabled");
        double? br = b.Num("brightness");
        foreach (var (_, lamp) in l)
        {
            if (enabled.HasValue) lamp.IsEnabled = enabled.Value;
            if (br.HasValue) lamp.BrightnessLevel = Math.Clamp(br.Value, 0, 100) / 100.0;
        }
        return await BacklightGet(b);
    }
}

// ---------- חשמל וסוללה ----------

/// <summary>מצב סוללה, תוכנית צריכה, זמני כיבוי מסך והשהיה, וסף חיסכון בסוללה (powercfg).
/// הפענוח אינו תלוי בשפת המערכת: GUID ומספרים בלבד</summary>
public static class Energy
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLine, BatteryFlag, Percent, SaverFlag;
        public int LifeTime, FullLifeTime;
    }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus s);

    private const string SubVideo = "SUB_VIDEO", ScreenOff = "VIDEOIDLE";
    private const string SubSleep = "SUB_SLEEP", SleepAfter = "STANDBYIDLE";
    private const string SubSaver = "de830923-a562-41af-a086-e3a2c6bad2da", SaverThreshold = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";
    private static readonly Regex GuidRe = new(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$");
    private static readonly Regex PlanRe = new(@"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\((.*?)\)(\s*\*)?");
    private static readonly Regex HexRe = new(@"0x([0-9a-fA-F]{8})");

    private static string Pc(string args) => Config.Run("cmd.exe", "/c chcp 65001 >nul & powercfg " + args, 15000);

    private static List<(string guid, string name, bool active)> Plans()
    {
        var res = new List<(string, string, bool)>();
        foreach (Match m in PlanRe.Matches(Pc("/list")))
            res.Add((m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(), m.Groups[3].Success));
        return res;
    }

    // מחזיר (ac, dc) בערכי ההגדרה, או null אם ההגדרה לא קיימת במחשב
    private static (int ac, int dc)? Query(string sub, string setting)
    {
        var ms = HexRe.Matches(Pc("/query SCHEME_CURRENT " + sub + " " + setting));
        if (ms.Count < 2) return null;
        int ac = Convert.ToInt32(ms[ms.Count - 2].Groups[1].Value, 16);
        int dc = Convert.ToInt32(ms[ms.Count - 1].Groups[1].Value, 16);
        return (ac, dc);
    }

    public static object Get()
    {
        bool has = GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 255 && (ps.BatteryFlag & 128) == 0;
        object battery = null;
        if (has)
            battery = new
            {
                percent = (int)ps.Percent,
                charging = ps.AcLine == 1,
                saverOn = ps.SaverFlag == 1,
                minutesLeft = ps.LifeTime >= 0 ? ps.LifeTime / 60 : (int?)null,
            };

        var plans = Plans();
        var screen = Query(SubVideo, ScreenOff);
        var sleep = Query(SubSleep, SleepAfter);
        var saver = Query(SubSaver, SaverThreshold);
        return new
        {
            battery,
            plans = plans.Select(p => new { id = p.guid, name = p.name, active = p.active }).ToList(),
            screenOff = screen.HasValue ? new { ac = screen.Value.ac, dc = screen.Value.dc } : null,
            sleepAfter = sleep.HasValue ? new { ac = sleep.Value.ac, dc = sleep.Value.dc } : null,
            saverThreshold = saver.HasValue ? (int?)Math.Clamp(saver.Value.dc, 0, 100) : null,
        };
    }

    private static int? Secs(JsonElement b, string name)
    {
        double? v = b.Num(name);
        return v.HasValue ? (int)Math.Clamp(Math.Round(v.Value), 0, 24 * 3600) : null;
    }

    public static object Set(JsonElement b)
    {
        string plan = b.Str("plan");
        if (!string.IsNullOrEmpty(plan))
        {
            if (!GuidRe.IsMatch(plan) || !Plans().Any(p => p.guid == plan.ToLowerInvariant()))
                throw BridgeException.NotFound("תוכנית הצריכה לא נמצאה");
            Pc("/setactive " + plan);
        }

        void Apply(string kind, string sub, string setting, int? v)
        {
            if (v.HasValue) Pc("/set" + kind + "valueindex SCHEME_CURRENT " + sub + " " + setting + " " + v.Value);
        }
        Apply("ac", SubVideo, ScreenOff, Secs(b, "screenOffAc"));
        Apply("dc", SubVideo, ScreenOff, Secs(b, "screenOffDc"));
        Apply("ac", SubSleep, SleepAfter, Secs(b, "sleepAfterAc"));
        Apply("dc", SubSleep, SleepAfter, Secs(b, "sleepAfterDc"));

        double? th = b.Num("saverThreshold");
        if (th.HasValue)
        {
            if (Query(SubSaver, SaverThreshold) == null) throw BridgeException.Unsupported("חיסכון בסוללה אינו נתמך במחשב זה");
            int v = (int)Math.Clamp(Math.Round(th.Value), 0, 100);
            Pc("/setdcvalueindex SCHEME_CURRENT " + SubSaver + " " + SaverThreshold + " " + v);
        }
        Pc("/setactive SCHEME_CURRENT");
        return Get();
    }
}
