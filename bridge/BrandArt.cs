using System.Drawing;

namespace OtzariaBridge;

/// <summary>סמל "אחת שאלתי" כתמונה (סבב B2) — נטען פעם אחת מאייקון היישום (assets/icon.ico, 256px) ומשמש את שומר המסך.</summary>
internal static class BrandArt
{
    private static Bitmap _big;
    public static Bitmap Big
    {
        get
        {
            if (_big != null) return _big;
            try { _big = new Icon(Overlay.AppIcon(), 256, 256).ToBitmap(); } catch { _big = new Bitmap(1, 1); }
            return _big;
        }
    }
}
