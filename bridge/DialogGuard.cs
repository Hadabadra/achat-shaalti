using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;

namespace OtzariaBridge;

/// <summary>
/// אכיפת מדיניות סייר הקבצים (FxPolicy/PathPolicy) וסוגי הקבצים המותרים (Kiosk.AllowedExtensions) על
/// דיאלוגי "פתיחה/שמירה" המקוריים של Windows, הנפתחים בכל תהליך במחשב — לא רק בתוך סייר הקבצים הפנימי שלנו.
///
/// הבעיה: FxPolicy/PathPolicy כבר נאכפים נכון בתוך הגשר עצמו (FsApi/RecycleApi) ובתוך תוסף "סייר קבצים"
/// הפנימי שלנו, אבל זו הגבלה על ה-UI שלנו בלבד — FxPolicy אינו ACL/minifilter ברמת מערכת ההפעלה. כל
/// תוכנה חיצונית שהמדיניות מתירה להריץ (למשל Word, ר' Programs.cs) יכולה להציג דיאלוג "פתיחת/שמירת
/// קובץ" המקורי של Windows (GetOpenFileName/IFileOpenDialog) דרך "קובץ ← פתיחה ← עיון" — דיאלוג כזה
/// מאפשר ניווט חופשי בכל מערכת הקבצים ובחירת כל סוג קובץ, ועוקף הן את FxPolicy והן את מגבלת הסיומות
/// של הקיוסק (Kiosk.AllowedExtensions, שהיום נאכפת רק ב-FsApi.Open הפנימי) לגמרי.
///
/// שיטה: SetWinEventHook גלובלי (כל התהליכים, לא רק שלנו) על EVENT_SYSTEM_DIALOGSTART ו-EVENT_OBJECT_SHOW,
/// מסונן מראש לפי class name "#32770" (הדיאלוג הסטנדרטי של Windows — גם Message-Box, גם Open/Save, גם
/// דיאלוגים אחרים). מאמתים ב-UI Automation שזה אכן דיאלוג פתיחה/שמירה מבוסס Explorer (ולא, למשל, הודעת
/// שגיאה) לפי נוכחות רכיבים ידועים (תצוגת הקבצים/סרגל הניווט). כל עוד הדיאלוג פתוח — polling קבוע קורא
/// את תיבת "שם הקובץ"/"סוג הקובץ", מקנן נתיב מלא דרכה (אם קיים) בדיוק עם PathPolicy.Resolve (אותה קנוניזציה
/// בדיוק כמו FsApi — לא נכתבה כאן שום לוגיקת קנוניזציה חדשה) ובודק מול FxPolicy.IsPathAllowed, וגם בודק את
/// סיומת שם-הקובץ המוצג מול Kiosk.DialogGuardAllowedExtensions ורשימת סיומות מסוכנות קבועה. חריגה ממדיניות
/// כלשהי -> סוגרים את הדיאלוג (WM_CLOSE) ומציגים התראה במגש. ר' "מגבלות ידועות" למטה — זו אכיפה אמינה
/// ומשמעותית, אבל לא אטומה ב-100%, ואין לשווק אותה ככזו.
///
/// למה לא תהליך עזר נפרד (--dialog-guard) כמוצע במשימה המקורית: הבעיה שפתרון כזה נועד לפתור (השירות
/// HardLockService רץ כ-SYSTEM בסשן 0 ולא יכול לגעת בדסקטופ האינטראקטיבי) כבר פתורה במבנה הקיים: הגשר
/// הרגיל (לא --service) כבר רץ באופן קבוע בכל סשן משתמש אינטראקטיבי (Installer.cs קובע Run key שמפעיל
/// אותו בכל כניסה; HardLockService.EnsureBridgeInSessions מוודא שהוא חוזר תוך שניות אם נסגר כש-HardLock
/// פעיל). יש לו כבר לולאת הודעות STA (Tray.Run -> Application.Run) בדיוק כפי ש-SetWinEventHook עם
/// WINEVENT_OUTOFCONTEXT דורש, וגישה ישירה (בתוך-תהליך) ל-FxPolicy/PathPolicy/Kiosk בלי IPC נוסף.
///
/// מגבלות ידועות (לא 100% אטום — ראו גם SECURITY_NOTES.md):
/// 1. דיאלוגים שאינם מבוססי comdlg32/IFileDialog הסטנדרטי (class "#32770") — דיאלוגי "פתיחה/שמירה"
///    מותאמים-אישית של תוכנה מסוימת שאינם משתמשים ברכיב המשותף של Windows — לא ייתפסו כלל (אין class
///    name תואם). זה חור ממשי: תוכנה לא-סטנדרטית יכולה לממש דיאלוג משלה שלא נתפס.
/// 2. זיהוי "דיאלוג פתיחה/שמירה אמיתי" (ולא Message-Box) ושליפת "תיבת שם הקובץ" (ולא "תיבת סוג הקובץ")
///    נשענים על AutomationId ("1000"/"1001") ועל סינון מחרוזות תבנית-סינון ("(*.ext)") שידועים/סבירים
///    לאורך גרסאות Windows, אך **לא נבדקו אינטראקטיבית מול דיאלוג חי** בסביבת הפיתוח הזו (אין כאן סשן
///    שולחן-עבודה אינטראקטיבי להרצת Inspect.exe/בדיקה חיה). יש לאמת זאת ידנית לפני אריזה — ר' הסעיף
///    שנוסף ל-docs/ROADMAP.md.
/// 3. קריאת "הנתיב הנוכחי" (להגבלת תיקיות, FxPolicy restricted) נשענת אך ורק על תוכן תיבת "שם הקובץ"
///    כשהוא נתיב מלא ותקין — לא על שחזור breadcrumb (תלוי-שפה/גרסה ומסוכן בכיוון ההפוך: אם היינו טועים
///    בפענוח הנתיב מה-breadcrumb ל"מותר" כשהוא בעצם אסור, זו הייתה התרת-גישה שגויה, גרוע בהרבה מ"חסימת
///    יתר"). המשמעות: במצב "מוגבל", דיאלוג שנפתח ונוצל בניווט-עכבר בלבד (בלי הקלדת/לחיצה שמאכלסת את תיבת
///    שם הקובץ בנתיב מלא) לא ייחסם לפי נתיב בפועל — רק בדיקת הסיומת עדיין פעילה עליו. זו אכיפה חלקית,
///    לא מלאה, במצב מוגבל.
/// 4. בדיקת הסיומת (קבוע "מסוכן" / רשימה מותרת) פועלת רק כש-Kiosk.IsActive או כש-FxPolicy אינו "open"
///    (ראו DetectViolation) — כשהכול כבוי זה "כבוי לגמרי", עקבי עם שאר המוצר. יש בה "דיביונס" (אותה
///    סיומת צריכה להופיע פעמיים ברצף, מרווח poll אחד, ~0.4 שניות) כדי לא לחסום דיאלוג רק כי המשתמש
///    באמצע הקלדת שם קובץ (למשל "report.d" באמצע הקלדת "report.docx"). זה מצמצם אך לא מבטל לחלוטין
///    סיכון לסגירה שגויה באמצע הקלדה מהירה/השהיה רגעית — תקלת UX אפשרית, לא פרצת אבטחה.
/// 5. **לא מומשה סינון/הסתרה של קבצים לא-רצויים מתוך תצוגת הרשימה עצמה** (לדוגמה: "להעלים" קובצי .exe
///    מרשימת הקבצים המוצגת בדיאלוג) — זה אינו ישים טכנית מבחוץ: תצוגת הרשימה היא רכיב shell מקורי
///    שמאוכלס ע"י התהליך הבעלים של הדיאלוג (comdlg32/IFileDialog + IShellFolder), ול-UI Automation של
///    תהליך צופה אחר אין API נתמך "להסיר" ממנה פריטים. לכן המימוש כאן חוסם רק בחירה/אישור (סוגר את כל
///    הדיאלוג) — לא מסנן תצוגה. זו בדיוק המגבלה שתועדה מראש כ"קבילה אם הסינון מהתצוגה לא ישים טכנית".
/// 6. בחירה מרובה (כמה קבצים יחד, "a.docx" "b.pdf") נבדקת פריט-פריט בצורה גסה (פיצול לפי מרכאות/רווח);
///    לא נבדק מול תרחיש בחירה מרובה אמיתי.
/// 7. חלון מרוץ (race) קצר: הבדיקה הראשונה רצה ~80 מ"ש אחרי האירוע, polling כל ~400 מ"ש אח"כ — יכול
///    להיות הבזק קצר של תוכן/בחירה אסורה לפני שהדיאלוג נסגר.
/// 8. מצב "נעול" (FxPolicy locked) סוגר כל דיאלוג Open/Save סטנדרטי מיידית, בלי תלות בנתיב/סיומת.
/// 9. אם תהליך הגשר בסשן המשתמש נסגר/קורס, האכיפה נעצרת עד שהוא עולה מחדש (תוך שניות, ר' Installer/
///    EnsureBridgeInSessions).
/// 10. **זה סוגר רק את נתיב "בחירת/פתיחת קובץ לא-רצוי דרך דיאלוג גרפי סטנדרטי".** זה *לא* פותר ערוצים
///     אחרים שבהם לתוכנה חיצונית מאושרת (Word למשל) יש גישה תכנותית למערכת הקבצים בלי לעבור כלל דרך
///     דיאלוג כזה — למשל מאקרו VBA בתוך Word עצמו (קוד לכל דבר, כולל קריאה/כתיבה של כל קובץ, כולל
///     config.json של הגשר) אינו עובר דרך שום דיאלוג "פתיחת קובץ" ולכן אינו נתפס כאן בכלל. ר' גם החולשה
///     שתועדה ב-PR #53 לגבי עריכת config.json ישירות בלי רכיב ההקשחה — DialogGuard אינו תיקון לה,
///     ואין להציג את שני אלה יחד כ"סגירת הפרצה באופן מלא".
/// </summary>
public static class DialogGuard
{
    private const string DialogClass = "#32770";
    private const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const uint WM_CLOSE = 0x0010;
    private const int PollMs = 400;

    // סיומות "מסוכנות": חסומות תמיד (לא ניתנות להתרה דרך kiosk.allowOpenExtensions) כל עוד מגבלה
    // כלשהי פעילה (Kiosk.IsActive או FxPolicy != open) — כולל json, כדי לכסות גם קבצי הגדרות כמו config.json
    private static readonly HashSet<string> DangerousExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "bat", "cmd", "ps1", "ps1xml", "psc1", "psd1", "psm1", "vbs", "vbe", "vb", "wsc",
        "js", "jse", "wsf", "wsh", "msi", "msc", "scr", "com", "exe", "cpl", "hta", "jar",
        "reg", "lnk", "url", "scf", "appref-ms", "dll", "sys", "inf", "json", "py", "pyw",
        "sh", "gadget", "application", "msp", "mst", "pif", "chm",
    };

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // שומרים את ה-delegate בשדה סטטי: אם ה-GC אוסף אותו, SetWinEventHook קורא לכתובת מתה -> קריסה
    private static WinEventDelegate _callback;
    private static IntPtr _hookDialogStart, _hookObjectShow;
    private static readonly ConcurrentDictionary<IntPtr, byte> _tracked = new();
    // דיביונס: אותה הפרה צריכה להופיע פעמיים ברצף (מרווח poll אחד) לפני שסוגרים בפועל (ר' מגבלה #4)
    private static readonly ConcurrentDictionary<IntPtr, string> _lastViolation = new();
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _callback = OnWinEvent;
            _hookDialogStart = SetWinEventHook(EVENT_SYSTEM_DIALOGSTART, EVENT_SYSTEM_DIALOGSTART, IntPtr.Zero,
                _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            // רשת ביטחון: לא כל תוכנה שולחת DIALOGSTART באופן אמין; EVENT_OBJECT_SHOW תמיד נורה כשחלון עולה
            _hookObjectShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
                _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (_hookDialogStart == IntPtr.Zero && _hookObjectShow == IntPtr.Zero)
                Config.Log("dialog-guard: SetWinEventHook נכשל לגמרי — אכיפת דיאלוגי קבצים מערכתית לא פעילה");
        }
        catch (Exception ex) { Config.Log("dialog-guard: עלייה נכשלה: " + ex.Message); }
    }

    private static void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (eventType == EVENT_OBJECT_SHOW && (idObject != OBJID_WINDOW || idChild != CHILDID_SELF)) return;
            var sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return;
            if (sb.ToString() != DialogClass) return;
            if (!_tracked.TryAdd(hwnd, 0)) return; // כבר במעקב
            Task.Run(() => MonitorDialog(hwnd));
        }
        catch (Exception ex) { Config.Log("dialog-guard event: " + ex.Message); }
    }

    /// <summary>רץ ברקע לכל דיאלוג #32770 חדש: מאמת שזה Open/Save ואז אוכף עד שהוא נסגר</summary>
    private static void MonitorDialog(IntPtr hwnd)
    {
        try
        {
            Thread.Sleep(80); // נותנים לעץ ה-UI Automation של הדיאלוג זמן להתאכלס לפני הבדיקה הראשונה
            if (!IsWindow(hwnd)) return;

            IUIAutomation ui;
            try { ui = new CUIAutomation8(); }
            catch { ui = new CUIAutomation(); }

            if (!LooksLikeFileDialog(ui, hwnd)) return; // Message-Box או דיאלוג סטנדרטי אחר — לא נוגעים בו
            Config.Log("dialog-guard: זוהה דיאלוג פתיחה/שמירה (hwnd=0x" + hwnd.ToString("X") + ")");

            while (IsWindow(hwnd))
            {
                string mode = FxPolicy.EffectiveMode();
                string violation = DetectViolation(ui, hwnd, mode, out string message);
                if (violation == null) { _lastViolation.TryRemove(hwnd, out _); Thread.Sleep(PollMs); continue; }

                // "נעול" אינו תלוי בטקסט שהמשתמש מקליד — אין צורך/טעם בדיביונס, סוגרים מיד
                if (violation == "locked" || _lastViolation.TryGetValue(hwnd, out var prev) && prev == violation)
                {
                    Close(hwnd, message);
                    return;
                }
                _lastViolation[hwnd] = violation;
                Thread.Sleep(PollMs);
            }
        }
        catch (Exception ex) { Config.Log("dialog-guard monitor: " + ex.Message); }
        finally { _tracked.TryRemove(hwnd, out _); _lastViolation.TryRemove(hwnd, out _); }
    }

    /// <summary>null = אין הפרה כרגע. אחרת מחזיר "מפתח הפרה" יציב (לדיביונס) ומציב את הודעת הטוסט ב-message</summary>
    private static string DetectViolation(IUIAutomation ui, IntPtr hwnd, string mode, out string message)
    {
        message = null;
        bool restrictingNow = Kiosk.IsActive || mode != "open";
        List<string> values = ReadCandidateValues(ui, hwnd);

        if (restrictingNow)
        {
            var allowed = Kiosk.DialogGuardAllowedExtensions();
            foreach (string raw in values)
            {
                string ext = ExtensionOf(raw);
                if (ext == null) continue;
                if (DangerousExt.Contains(ext))
                {
                    message = Lang.T($"סוג קובץ זה (.{ext}) חסום לבחירה מטעמי אבטחה.",
                                      $"This file type (.{ext}) can't be selected for security reasons.");
                    return "ext:" + ext;
                }
                if (!allowed.Contains(ext))
                {
                    message = Lang.T($"סוג קובץ זה (.{ext}) אינו ברשימת סוגי הקבצים המותרים כרגע.",
                                      $"This file type (.{ext}) isn't in the currently allowed list.");
                    return "ext:" + ext;
                }
            }
        }

        if (mode == "locked")
        {
            message = Lang.T("סייר הקבצים נעול — אי אפשר להשתמש כרגע בדיאלוגי פתיחה/שמירה של קבצים.",
                              "The file browser is locked — file open/save dialogs are blocked right now.");
            return "locked";
        }

        if (mode == "restricted")
        {
            // מקור האמין היחיד שמומש לנתיב — נתיב מילולי מלא (לא breadcrumb, ר' מגבלה #3)
            string path = values.FirstOrDefault(v => IsFullPath(v));
            bool ok = path != null && FxPolicy.IsPathAllowed(path.Trim());
            if (!ok && path != null)
            {
                message = Lang.T("הגישה לתיקייה הזו חסומה במדיניות סייר הקבצים.",
                                  "Access to this folder is blocked by the file-browser policy.");
                return "path:" + path.Trim().ToLowerInvariant();
            }
        }

        return null;
    }

    private static bool IsFullPath(string v)
    {
        try { return !string.IsNullOrWhiteSpace(v) && Path.IsPathFullyQualified(v.Trim()); }
        catch { return false; }
    }

    /// <summary>סיומת קובץ מתוך ערך גולמי של תיבת "שם הקובץ" (יכול להיות נתיב מלא, שם בלבד, או "a" "b" מרובה)</summary>
    private static string ExtensionOf(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        // תבנית סינון סוג-קובץ ("Word Document (*.docx)") — לא שם קובץ, מתעלמים (ר' למה למטה ב-ReadCandidateValues)
        if (s.Contains("(*.", StringComparison.Ordinal)) return null;
        if (s.Length >= 2 && s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end > 0) s = s.Substring(1, end - 1);
        }
        else
        {
            int sp = s.IndexOf(' ');
            // בחירה מרובה גסה: "report.docx summary.pdf" (בלי מרכאות) — בודקים רק את הראשון; טיפול גס, ר' מגבלה #6
            if (sp > 0 && !IsFullPath(s)) s = s.Substring(0, sp);
        }
        try
        {
            string ext = Path.GetExtension(s).TrimStart('.');
            return string.IsNullOrEmpty(ext) ? null : ext;
        }
        catch { return null; }
    }

    /// <summary>
    /// זיהוי "זה דיאלוג פתיחה/שמירה מבוסס Explorer" לפי AutomationId יציב לאורך גרסאות Windows:
    /// "1000" (תצוגת רשימת הקבצים/התיקיות המשותפת) או "1001" (סרגל הניווט/breadcrumb). לא נבדק
    /// אינטראקטיבית מול דיאלוג חי בסביבת הפיתוח הזו — ר' מגבלה #2 בתיעוד המחלקה.
    /// </summary>
    internal static bool LooksLikeFileDialog(IUIAutomation ui, IntPtr hwnd)
    {
        try
        {
            IUIAutomationElement root = ui.ElementFromHandle(hwnd);
            if (root == null) return false;
            var c1 = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1000");
            var c2 = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1001");
            var or = ui.CreateOrCondition(c1, c2);
            return root.FindFirst(TreeScope.TreeScope_Descendants, or) != null;
        }
        catch (Exception ex) { Config.Log("dialog-guard detect: " + ex.Message); return false; }
    }

    /// <summary>
    /// כל הערכים הטקסטואליים (לא ריקים) מרכיבי Edit/ComboBox בדיאלוג — כולל גם תיבת "סוג הקובץ" (לכן
    /// ExtensionOf מסנן ערכים בתבנית "(*.ext)" בנפרד, ר' שם) וגם תיבת "שם הקובץ" עצמה.
    /// </summary>
    private static List<string> ReadCandidateValues(IUIAutomation ui, IntPtr hwnd)
    {
        var result = new List<string>();
        try
        {
            IUIAutomationElement root = ui.ElementFromHandle(hwnd);
            if (root == null) return result;
            var condEdit = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_ControlTypePropertyId, (int)UIA_ControlTypeIds.UIA_EditControlTypeId);
            var condCombo = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_ControlTypePropertyId, (int)UIA_ControlTypeIds.UIA_ComboBoxControlTypeId);
            var or = ui.CreateOrCondition(condEdit, condCombo);
            IUIAutomationElementArray arr = root.FindAll(TreeScope.TreeScope_Descendants, or);
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                string val = GetElementValue(arr.GetElement(i));
                if (!string.IsNullOrWhiteSpace(val)) result.Add(val);
            }
        }
        catch (Exception ex) { Config.Log("dialog-guard read-values: " + ex.Message); }
        return result;
    }

    private static string GetElementValue(IUIAutomationElement el)
    {
        try
        {
            if (el?.GetCurrentPattern((int)UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern vp)
            {
                string v = vp.CurrentValue;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }
        try
        {
            if (el?.GetCurrentPattern((int)UIA_PatternIds.UIA_LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern lp)
            {
                string v = lp.CurrentValue;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }
        return null;
    }

    private static void Close(IntPtr hwnd, string message)
    {
        try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
        Config.Log("dialog-guard: נחסם דיאלוג קבצים חורג ממדיניות (hwnd=0x" + hwnd.ToString("X") + "): " + message);
        Tray.Notify(Lang.T("גישה לקבצים נחסמה", "File access blocked"), message);
    }
}
