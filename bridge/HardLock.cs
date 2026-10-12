using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// נעילה מוקשחת (בעיה #1, גרסה 3.0.0) — רכיב שירות Windows שרץ כ-SYSTEM.
///
/// הגשר הרגיל רץ בהרשאות המשתמש, ולכן משתמש נעול יכול (בעקיפין) לסגור אותו או לערוך את config.json.
/// השירות OtzariaBridgeService, כשהוא מותקן (אופציונלי, דורש אישור מנהל פעם אחת):
///  • מחזיק את מצב הנעילה ואת גיבוב הסיסמה ב-%ProgramData%\OtzariaBridge (רק SYSTEM ומנהלים כותבים).
///  • מפעיל מחדש את הגשר בכל חשבון מחובר אם נסגר — כולל חשבון Windows אחר.
///  • מחזיר מדיניות (מנהל המשימות ועורך הרישום חסומים) אם נמחקה, בכל החשבונות המחוברים.
///  • חוסם התקני אחסון USB (אופציונלי).
/// השירות לא נוגע במעטפת Windows ולא בכניסה למערכת (לקח מ-#8), ואינו רץ במצב בטוח.
/// הגשר מדבר איתו דרך Named Pipe מקומי.
/// </summary>
public static class HardLock
{
    public const string ServiceName = "OtzariaBridgeService";
    public const string PipeName = "OtzariaBridgeService";

    public static string ProgDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OtzariaBridge");
    public static string ServiceExe => Path.Combine(ProgDir, "OtzariaBridge.exe");
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OtzariaBridge");

    // ================= צד הגשר (משתמש) =================

    public static void Register()
    {
        Server.Route("/kiosk/hard/status", _ => Status(fresh: true));
        Server.Route("/kiosk/hard/install", Install);
        Server.Route("/kiosk/hard/uninstall", Uninstall);
        Server.Route("/kiosk/hard/settings", Settings);
    }

    /// <summary>שליחת פקודה לשירות. null = השירות לא זמין</summary>
    public static JsonObject Call(JsonObject req, int timeoutMs = 2500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(pipe, Encoding.UTF8);
            w.WriteLine(req.ToJsonString());
            var readTask = r.ReadLineAsync();
            if (!readTask.Wait(Math.Max(timeoutMs, 15000))) return null;
            return JsonNode.Parse(readTask.Result ?? "") as JsonObject;
        }
        catch { return null; }
    }

    /// <summary>תשובת שגיאה מהשירות → חריגה עם אותו קוד</summary>
    private static JsonObject Must(JsonObject res)
    {
        if (res == null) throw new BridgeException("unsupported", "רכיב ההקשחה אינו זמין", 503);
        if (res["ok"]?.GetValue<bool>() == true) return res;
        string code = res["code"]?.ToString() ?? "internal";
        int status = code switch { "wrong_password" => 401, "too_many" => 429, "bad_request" => 400, _ => 500 };
        throw new BridgeException(code == "too_many" ? "wrong_password" : code, res["message"]?.ToString() ?? "שגיאה ברכיב ההקשחה", status);
    }

    private static JsonObject _cache;
    private static bool _installed;
    private static DateTime _installedAt = DateTime.MinValue;
    private static DateTime _cacheAt = DateTime.MinValue;

    public static bool ServiceInstalled()
    {
        try { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private static JsonObject ServiceStatus(bool fresh = false)
    {
        if (!fresh && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(3)) return _cache;
        // בלי שירות מותקן לא ממתינים לחיבור (בדיקת ההתקנה נשמרת ל-30 שניות)
        if (DateTime.UtcNow - _installedAt > TimeSpan.FromSeconds(fresh ? 0 : 30)) { _installed = ServiceInstalled(); _installedAt = DateTime.UtcNow; }
        _cache = _installed ? Call(new JsonObject { ["op"] = "status" }, 1000) : null;
        _cacheAt = DateTime.UtcNow;
        return _cache;
    }

    /// <summary>השירות מותקן, רץ ויש לו סיסמה — מקור האמת לסיסמה ולמצב הנעילה</summary>
    public static bool Available => ServiceStatus()?["configured"]?.GetValue<bool>() == true;

    /// <summary>האם השירות אומר שהנעילה פעילה</summary>
    public static bool ServiceActive => ServiceStatus()?["active"]?.GetValue<bool>() == true;

    public static object Status(bool fresh = false)
    {
        var s = ServiceStatus(fresh);
        bool installed = s != null || ServiceInstalled();
        string sv = s?["version"]?.ToString();
        return new
        {
            installed,
            running = s != null,
            configured = s?["configured"]?.GetValue<bool>() == true,
            active = s?["active"]?.GetValue<bool>() == true,
            blockUsbStorage = s?["blockUsbStorage"]?.GetValue<bool>() == true,
            version = sv,
            needsUpdate = sv != null && sv != Program.Version,
            sessions = s?["sessions"]?.GetValue<int>() ?? 0,
        };
    }

    public static void Verify(string password, bool allowRecovery) =>
        Must(Call(new JsonObject { ["op"] = "verify", ["password"] = password ?? "", ["allowRecovery"] = allowRecovery }));

    public static void Enable(string password, string otzariaPath)
    {
        Must(Call(new JsonObject { ["op"] = "enable", ["password"] = password ?? "", ["otzariaPath"] = otzariaPath ?? "" }));
        _cacheAt = DateTime.MinValue;
        _sawActive = true;
    }

    public static void Disable(string password)
    {
        Must(Call(new JsonObject { ["op"] = "disable", ["password"] = password ?? "" }));
        _cacheAt = DateTime.MinValue;
        _sawActive = false;
    }

    public static void SetPassword(string oldPassword, string newPassword) =>
        Must(Call(new JsonObject { ["op"] = "set-password", ["old"] = oldPassword ?? "", ["new"] = newPassword ?? "" }));

    /// <summary>קוד שחזור משורת הפקודה: מבטל גם בשירות</summary>
    public static bool EmergencyDisable(string code)
    {
        var r = Call(new JsonObject { ["op"] = "disable", ["password"] = code ?? "" });
        return r?["ok"]?.GetValue<bool>() == true;
    }

    public static string UninstallWithPassword(string password)
    {
        var r = Call(new JsonObject { ["op"] = "uninstall", ["password"] = password ?? "" }, 5000);
        if (r == null) return null; // אין שירות
        return r["ok"]?.GetValue<bool>() == true ? null : r["message"]?.ToString() ?? "סיסמה שגויה";
    }

    private static object Install(JsonElement b)
    {
        if (string.IsNullOrEmpty(Config.Get<string>("kiosk.passwordHash", null)))
            throw BridgeException.BadRequest("יש להגדיר סיסמת נעילה לפני התקנת רכיב ההקשחה");
        Kiosk.RequirePassword(b.Str("password"));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--install-service")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (!p.WaitForExit(180_000)) throw new BridgeException("timeout", "ההתקנה לא הסתיימה בזמן", 504);
            if (p.ExitCode != 0)
                throw new BridgeException("internal", "התקנת רכיב ההקשחה נכשלה (קוד " + p.ExitCode + "). פרטים ב-" + Path.Combine(DataDir, "service.log"), 500);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new BridgeException("cancelled", "ההתקנה בוטלה (לא אושרה הרשאת מנהל)", 400);
        }

        // ממתינים שהשירות יעלה, ואז מעבירים לו את גיבובי הסיסמה וקוד השחזור (רק בהתקנה ראשונה)
        JsonObject st = null;
        for (int i = 0; i < 20 && st == null; i++) { st = Call(new JsonObject { ["op"] = "status" }, 1000); if (st == null) Thread.Sleep(500); }
        if (st == null) throw new BridgeException("internal", "השירות הותקן אבל לא עלה", 500);
        if (st["configured"]?.GetValue<bool>() != true)
        {
            Must(Call(new JsonObject
            {
                ["op"] = "setup",
                ["passwordHash"] = Config.Get<string>("kiosk.passwordHash", ""),
                ["salt"] = Config.Get<string>("kiosk.salt", ""),
                ["recoveryHash"] = Config.Get<string>("kiosk.recoveryHash", ""),
                ["recoverySalt"] = Config.Get<string>("kiosk.recoverySalt", ""),
            }));
        }
        if (Kiosk.IsActive) Enable(b.Str("password"), Kiosk.OtzariaPath());
        Config.Log("hardlock: רכיב ההקשחה הותקן/עודכן");
        return Status(fresh: true);
    }

    private static object Uninstall(JsonElement b)
    {
        Must(Call(new JsonObject { ["op"] = "uninstall", ["password"] = b.Str("password") ?? "" }, 5000));
        _sawActive = false;
        Thread.Sleep(1500);
        return Status(fresh: true);
    }

    private static object Settings(JsonElement b)
    {
        var req = new JsonObject { ["op"] = "settings", ["password"] = b.Str("password") ?? "" };
        if (b.Has("blockUsbStorage")) req["blockUsbStorage"] = b.Bool("blockUsbStorage");
        Must(Call(req));
        return Status(fresh: true);
    }

    // ---------- סנכרון: השירות הוא מקור האמת ----------

    private static System.Threading.Timer _sync;
    private static bool _sawActive;

    /// <summary>כל 5 שניות: אם השירות אומר "נעול" והגשר לא — נועלים (גם אחרי עריכת config.json או בחשבון אחר)</summary>
    public static void StartSync()
    {
        _sync ??= new System.Threading.Timer(_ =>
        {
            try
            {
                var s = ServiceStatus(fresh: true);
                if (s == null) return;
                bool active = s["active"]?.GetValue<bool>() == true;
                if (active)
                {
                    _sawActive = true;
                    if (!Kiosk.IsActive) Kiosk.ForceActivate(s["otzariaPath"]?.ToString());
                }
                else if (_sawActive)
                {
                    _sawActive = false;
                    if (Kiosk.IsActive) Kiosk.ReleaseFromService();
                }
            }
            catch (Exception ex) { Config.Log("hardlock sync: " + ex.Message); }
        }, null, 3000, 5000);
    }

    // ================= התקנת השירות (רץ בהרשאות מנהל) =================

    public static int InstallService()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            SecureDir(DataDir);
            SvcLog("install: מתחיל, גרסה " + Program.Version);

            bool exists = ServiceInstalled();
            if (exists) StopService();

            Directory.CreateDirectory(ProgDir);
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(ServiceExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(Environment.ProcessPath!, ServiceExe, overwrite: true);

            string bin = "\"" + ServiceExe + "\" --service";
            if (!exists)
                Sc($"create {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto DisplayName= \"תורת אסתר\"");
            else
                Sc($"config {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto DisplayName= \"תורת אסתר\"");
            Sc($"description {ServiceName} \"תורת אסתר — על שם אסתר דינר ע״ה. נעילה מוקשחת לגשר אוצריא\"");
            Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000");

            using var sc = new ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            SvcLog("install: השירות רץ");
            return 0;
        }
        catch (Exception ex)
        {
            SvcLog("install failed: " + ex);
            return 3;
        }
    }

    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception ex) { SvcLog("stop: " + ex.Message); }
        Thread.Sleep(500);
    }

    private static void Sc(string args)
    {
        string outp = Config.Run("sc.exe", args, 20000);
        SvcLog("sc " + args.Split(' ')[0] + ": " + outp.Trim().Replace("\r\n", " "));
    }

    /// <summary>התיקייה: SYSTEM ומנהלים — שליטה מלאה; משתמשים — קריאה בלבד</summary>
    private static void SecureDir(string dir)
    {
        // תיקייה שנוצרה מראש בידי משתמש רגיל (הוא נשאר הבעלים ויכול לשנות הרשאות) — מוחקים ויוצרים מחדש
        try
        {
            var owner = new DirectoryInfo(dir).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner != null && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid) && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            {
                Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex) { SvcLog("secure-dir owner: " + ex.Message); }
        var ds = new DirectorySecurity();
        ds.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        ds.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(ds);
    }

    internal static void SvcLog(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string f = Path.Combine(DataDir, "service.log");
            if (File.Exists(f) && new FileInfo(f).Length > 2_000_000) File.Delete(f);
            File.AppendAllText(f, DateTime.Now.ToString("s") + " " + line + Environment.NewLine);
        }
        catch { }
    }
}

/// <summary>השירות עצמו (LocalSystem). מופעל עם OtzariaBridge.exe --service</summary>
public class HardLockService : ServiceBase
{
    private const int Iterations = 200_000;
    private const string PoliciesSub = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string UsbStorKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
    private static readonly Regex UserSid = new(@"^S-1-5-21-[\d-]+$", RegexOptions.Compiled);

    private readonly object _gate = new();
    private JsonObject _state;
    private System.Threading.Timer _timer;
    private CancellationTokenSource _cts;
    private int _failures;
    private DateTime _lockoutUntil = DateTime.MinValue;
    private readonly Dictionary<int, DateTime> _sessionSeen = new();
    private readonly Dictionary<int, DateTime> _lastLaunch = new();
    private int _sessions;

    private static string StateFile => Path.Combine(HardLock.DataDir, "hardlock.json");

    public HardLockService()
    {
        ServiceName = HardLock.ServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        Load();
        _cts = new CancellationTokenSource();
        for (int i = 0; i < 3; i++) Task.Run(() => PipeLoop(_cts.Token));
        _timer = new System.Threading.Timer(_ => Tick(), null, 2000, 5000);
        HardLock.SvcLog("service: עלה (גרסה " + Program.Version + "), נעילה " + (Active ? "פעילה" : "כבויה"));
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        HardLock.SvcLog("service: נעצר");
    }

    protected override void OnShutdown() => OnStop();

    // ---------- מצב ----------

    private bool Active => _state["active"]?.GetValue<bool>() == true;
    private bool Configured => !string.IsNullOrEmpty(_state["passwordHash"]?.ToString());

    private void Load()
    {
        try { _state = File.Exists(StateFile) ? JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject : null; }
        catch (Exception ex) { HardLock.SvcLog("load: " + ex.Message); }
        _state ??= new JsonObject();
    }

    private void Save()
    {
        Directory.CreateDirectory(HardLock.DataDir);
        string tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, _state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StateFile, true);
    }

    private static string Hash(string secret, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32));

    private bool Matches(string secret, string hashKey, string saltKey)
    {
        string hash = _state[hashKey]?.ToString();
        string salt = _state[saltKey]?.ToString();
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(secret)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(hash),
                Convert.FromBase64String(Hash(secret, Convert.FromBase64String(salt))));
        }
        catch { return false; }
    }

    private static string NormalizeRecovery(string s) => (s ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

    /// <summary>null = תקין; אחרת תשובת שגיאה</summary>
    private JsonObject Check(string password, bool allowRecovery)
    {
        if (!Configured) return Err("bad_request", "רכיב ההקשחה עדיין לא הוגדר");
        if (DateTime.UtcNow < _lockoutUntil)
            return Err("too_many", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {(int)Math.Ceiling((_lockoutUntil - DateTime.UtcNow).TotalSeconds)} שניות");
        bool ok = Matches(password, "passwordHash", "salt") ||
                  (allowRecovery && Matches(NormalizeRecovery(password), "recoveryHash", "recoverySalt"));
        if (ok) { _failures = 0; return null; }
        if (++_failures >= 5) { _failures = 0; _lockoutUntil = DateTime.UtcNow.AddSeconds(60); }
        return Err("wrong_password", "סיסמה שגויה");
    }

    private static JsonObject Err(string code, string message) => new() { ["ok"] = false, ["code"] = code, ["message"] = message };
    private static JsonObject Ok() => new() { ["ok"] = true };

    // ---------- Named Pipe ----------

    private async Task PipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ps = new PipeSecurity();
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                var pipe = NamedPipeServerStreamAcl.Create(HardLock.PipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, ps);
                try { await pipe.WaitForConnectionAsync(ct); }
                catch { pipe.Dispose(); throw; }
                // ביקורת #30 ממצא 5: כל חיבור מטופל בנפרד, עם מגבלת אורך ודדליין, כך שלקוח תקוע
                // לא חוסם את הבאים אחריו (ולא דוחף את הגשר ל-fallback של config.json)
                _ = Task.Run(() => ServeOne(pipe, ct));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { HardLock.SvcLog("pipe loop: " + ex.Message); await Task.Delay(1000); }
        }
    }

    private const int PipeMaxLine = 64 * 1024;

    private async Task ServeOne(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(5000);
            try
            {
                var buf = new byte[4096];
                var line = new MemoryStream();
                bool done = false;
                while (!done)
                {
                    int n = await pipe.ReadAsync(buf, 0, buf.Length, cts.Token);
                    if (n <= 0) break;
                    int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                    line.Write(buf, 0, nl < 0 ? n : nl);
                    if (line.Length > PipeMaxLine) { HardLock.SvcLog("pipe: line too long"); return; }
                    done = nl >= 0;
                }
                if (!done && line.Length == 0) return;
                string text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                JsonObject res;
                try { res = Handle(JsonNode.Parse(text.Length == 0 ? "{}" : text) as JsonObject ?? new JsonObject()); }
                catch (Exception ex) { res = Err("internal", ex.Message); HardLock.SvcLog("pipe: " + ex); }
                byte[] outb = new UTF8Encoding(false).GetBytes(res.ToJsonString() + "\n");
                await pipe.WriteAsync(outb, 0, outb.Length, cts.Token);
                await pipe.FlushAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { HardLock.SvcLog("pipe conn: " + ex.Message); }
        }
    }

    private JsonObject Handle(JsonObject q)
    {
        lock (_gate)
        {
            string op = q["op"]?.ToString() ?? "";
            string pw = q["password"]?.ToString();
            JsonObject e;
            switch (op)
            {
                case "status":
                    return new JsonObject
                    {
                        ["ok"] = true, ["version"] = Program.Version, ["configured"] = Configured, ["active"] = Active,
                        ["blockUsbStorage"] = _state["blockUsbStorage"]?.GetValue<bool>() == true,
                        ["otzariaPath"] = _state["otzariaPath"]?.ToString() ?? "", ["sessions"] = _sessions,
                    };

                case "setup":
                    if (Configured) return Err("bad_request", "כבר הוגדר");
                    foreach (var k in new[] { "passwordHash", "salt", "recoveryHash", "recoverySalt" })
                    {
                        string v = q[k]?.ToString();
                        if (string.IsNullOrEmpty(v)) return Err("bad_request", "חסר " + k);
                        _state[k] = v;
                    }
                    Save();
                    HardLock.SvcLog("service: הוגדרו סיסמה וקוד שחזור");
                    return Ok();

                case "verify":
                    return Check(pw, q["allowRecovery"]?.GetValue<bool>() == true) ?? Ok();

                case "enable":
                    if ((e = Check(pw, false)) != null) return e;
                    _state["active"] = true;
                    string otz = q["otzariaPath"]?.ToString();
                    if (!string.IsNullOrEmpty(otz)) _state["otzariaPath"] = otz;
                    Save();
                    HardLock.SvcLog("service: נעילה הופעלה");
                    Task.Run(Tick);
                    return Ok();

                case "disable":
                    if ((e = Check(pw, true)) != null) return e;
                    _state["active"] = false;
                    Save();
                    HardLock.SvcLog("service: נעילה בוטלה");
                    Task.Run(Tick);
                    return Ok();

                case "set-password":
                    if ((e = Check(q["old"]?.ToString(), true)) != null) return e;
                    string np = q["new"]?.ToString() ?? "";
                    if (np.Length < 6) return Err("bad_request", "הסיסמה חייבת להכיל לפחות 6 תווים");
                    byte[] salt = RandomNumberGenerator.GetBytes(16);
                    _state["salt"] = Convert.ToBase64String(salt);
                    _state["passwordHash"] = Hash(np, salt);
                    Save();
                    return Ok();

                case "settings":
                    if ((e = Check(pw, false)) != null) return e;
                    if (q["blockUsbStorage"] is JsonValue bu) _state["blockUsbStorage"] = bu.GetValue<bool>();
                    Save();
                    Task.Run(Tick);
                    return Ok();

                case "uninstall":
                    if (Configured && (e = Check(pw, true)) != null) return e;
                    _state["active"] = false;
                    _state["uninstalling"] = true;
                    Save();
                    Task.Run(SelfUninstall);
                    return Ok();

                default:
                    return Err("bad_request", "פקודה לא מוכרת: " + op);
            }
        }
    }

    // ---------- שומר ----------

    private int _tickBusy;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return;
        try
        {
            bool active, blockUsb;
            lock (_gate) { active = Active; blockUsb = _state["blockUsbStorage"]?.GetValue<bool>() == true; }
            EnforcePolicies(active);
            EnforceUsb(active && blockUsb);
            if (active) EnsureBridgeInSessions();
        }
        catch (Exception ex) { HardLock.SvcLog("tick: " + ex.Message); }
        finally { Interlocked.Exchange(ref _tickBusy, 0); }
    }

    /// <summary>מדיניות בכל חשבון מחובר (HKEY_USERS\SID): מנהל המשימות ועורך הרישום חסומים; מוחזרת אם נמחקה</summary>
    private void EnforcePolicies(bool active)
    {
        HashSet<string> touched;
        lock (_gate)
            touched = (_state["touched"] as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).ToHashSet()
                      ?? new HashSet<string>();
        bool changed = false;
        foreach (string sid in Registry.Users.GetSubKeyNames().Where(n => UserSid.IsMatch(n)))
        {
            try
            {
                if (active)
                {
                    using var k = Registry.Users.CreateSubKey(sid + "\\" + PoliciesSub);
                    foreach (var name in new[] { "DisableTaskMgr", "DisableRegistryTools" })
                        if (!(k.GetValue(name) is int v && v == 1)) k.SetValue(name, 1, RegistryValueKind.DWord);
                    if (touched.Add(sid)) changed = true;
                }
                else if (touched.Contains(sid))
                {
                    using var k = Registry.Users.OpenSubKey(sid + "\\" + PoliciesSub, true);
                    k?.DeleteValue("DisableTaskMgr", false);
                    k?.DeleteValue("DisableRegistryTools", false);
                    touched.Remove(sid);
                    changed = true;
                }
            }
            catch (Exception ex) { HardLock.SvcLog("policy " + sid + ": " + ex.Message); }
        }
        if (changed)
        {
            lock (_gate)
            {
                _state["touched"] = new JsonArray(touched.Select(s => (JsonNode)s).ToArray());
                Save();
            }
        }
    }

    private void EnforceUsb(bool block)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(UsbStorKey, true);
            if (k == null) return;
            bool hasOriginal;
            int orig;
            lock (_gate)
            {
                hasOriginal = _state["usbOriginal"] != null;
                orig = hasOriginal ? _state["usbOriginal"].GetValue<int>() : 3;
            }
            int cur = k.GetValue("Start") is int v ? v : 3;
            if (block && cur != 4)
            {
                lock (_gate) { _state["usbOriginal"] = cur; Save(); }
                k.SetValue("Start", 4, RegistryValueKind.DWord);
                HardLock.SvcLog("service: אחסון USB נחסם");
            }
            else if (!block && hasOriginal)
            {
                k.SetValue("Start", orig == 4 ? 3 : orig, RegistryValueKind.DWord);
                lock (_gate) { _state.Remove("usbOriginal"); Save(); }
                HardLock.SvcLog("service: אחסון USB שוחרר");
            }
        }
        catch (Exception ex) { HardLock.SvcLog("usb: " + ex.Message); }
    }

    /// <summary>בכל חשבון מחובר (כולל חשבון שני) הגשר חייב לרוץ; אם נסגר — מופעל מחדש תוך כמה שניות</summary>
    private void EnsureBridgeInSessions()
    {
        if (!File.Exists(HardLock.ServiceExe)) return;
        var running = Process.GetProcessesByName("OtzariaBridge").Select(p => { try { return p.SessionId; } catch { return -1; } }).ToHashSet();
        var active = Native.ActiveSessions();
        _sessions = active.Count;
        foreach (int sid in active)
        {
            if (!_sessionSeen.ContainsKey(sid)) _sessionSeen[sid] = DateTime.UtcNow;
            if (running.Contains(sid)) continue;
            // חסד אחרי הכניסה (הגשר עולה גם מ-Run של המשתמש), ולא יותר מניסיון אחד ב-20 שניות
            if (DateTime.UtcNow - _sessionSeen[sid] < TimeSpan.FromSeconds(15)) continue;
            if (_lastLaunch.TryGetValue(sid, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(20)) continue;
            _lastLaunch[sid] = DateTime.UtcNow;
            bool ok = Native.LaunchInSession(sid, "\"" + HardLock.ServiceExe + "\" --background --portable --from-service");
            HardLock.SvcLog("service: הגשר לא רץ בחשבון (session " + sid + ") — " + (ok ? "הופעל מחדש" : "ההפעלה נכשלה"));
        }
        foreach (int gone in _sessionSeen.Keys.Except(active).ToList()) _sessionSeen.Remove(gone);
    }

    private void SelfUninstall()
    {
        try
        {
            Thread.Sleep(500);
            EnforcePolicies(false);
            EnforceUsb(false);
            HardLock.SvcLog("service: הסרה");
            Config.Run("sc.exe", "delete " + HardLock.ServiceName, 10000);
            // מחיקת הקבצים אחרי שהשירות ייעצר
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping -n 6 127.0.0.1 >nul & rmdir /s /q \"{HardLock.ProgDir}\" & rmdir /s /q \"{HardLock.DataDir}\"")
                { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) { HardLock.SvcLog("uninstall: " + ex.Message); }
        Stop();
    }
}

/// <summary>הפעלת תהליך בחשבון של משתמש מחובר מתוך שירות SYSTEM</summary>
internal static class Native
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO { public int SessionId; public IntPtr WinStationName; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public static List<int> ActiveSessions()
    {
        var list = new List<int>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr p, out int n)) return list;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < n; i++)
            {
                var s = Marshal.PtrToStructure<WTS_SESSION_INFO>(p + i * size);
                if (s.State == 0 /* WTSActive */ && s.SessionId != 0) list.Add(s.SessionId);
            }
        }
        finally { WTSFreeMemory(p); }
        return list;
    }

    public static bool LaunchInSession(int sessionId, string commandLine)
    {
        if (!WTSQueryUserToken(sessionId, out IntPtr token)) return false;
        IntPtr env = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out env, token, false);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
            bool ok = CreateProcessAsUser(token, null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, HardLock.ProgDir, ref si, out var pi);
            if (ok) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
            return ok;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(token);
        }
    }
}
