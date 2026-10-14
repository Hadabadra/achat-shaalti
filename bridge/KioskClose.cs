using System;
using System.Runtime.InteropServices;

namespace OtzariaBridge;

/// <summary>
/// #10 (13.10.2026): בנעילה — משבית את פקודת הסגירה של חלון אוצריא (X בכותרת, Alt+F4, תפריט המערכת).
/// כפתור X שאוצריא מציירת בעצמה (Flutter) שולח WM_CLOSE ישירות — לכן זה לא חוסם אותו במאה אחוז;
/// במקרה כזה ה-watchdog מחזיר את אוצריא מיד. חסימה מלאה דורשת תמיכה מצד אוצריא (ראו ROADMAP).
/// </summary>
public static partial class Kiosk
{
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
    [DllImport("user32.dll")] private static extern bool EnableMenuItem(IntPtr hMenu, uint id, uint flags);
    private static IntPtr _closeBlocked = IntPtr.Zero;

    private static void BlockClose(IntPtr hwnd)
    {
        if (hwnd == _closeBlocked) return;
        try
        {
            var m = GetSystemMenu(hwnd, false);
            if (m != IntPtr.Zero) EnableMenuItem(m, 0xF060 /* SC_CLOSE */, 0x1 | 0x0 /* MF_GRAYED|MF_BYCOMMAND */);
            _closeBlocked = hwnd;
        }
        catch (Exception ex) { Config.Log("kiosk block close: " + ex.Message); }
    }

    /// <summary>מחזיר את פקודת הסגירה ביציאה מהנעילה</summary>
    internal static void UnblockClose()
    {
        try { if (_closeBlocked != IntPtr.Zero) GetSystemMenu(_closeBlocked, true); } catch { }
        _closeBlocked = IntPtr.Zero;
    }
}
