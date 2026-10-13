using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OtzariaBridge;

/// <summary>
/// #8/#9 (13.10.2026): מצב מנותק נלקח מאוצריא עצמה — אין הגדרה נפרדת.
/// קורא את קובצי ההעדפות של אוצריא (shared_preferences.json תחת %APPDATA% בתיקייה ששמה מכיל otzaria)
/// ומחפש מפתח בוליאני שמכיל "offline" ושערכו true. התוצאה נשמרת לדקה.
/// </summary>
public static class OfflineMode
{
    private static bool _cache;
    private static DateTime _at = DateTime.MinValue;

    public static void Register() => Server.Route("/sys/offline", _ => new { offline = IsOn() });

    public static bool IsOn()
    {
        if ((DateTime.UtcNow - _at).TotalSeconds < 60) return _cache;
        bool on = false;
        try
        {
            foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                string r = Environment.GetFolderPath(root);
                if (!Directory.Exists(r)) continue;
                var files = Directory.EnumerateDirectories(r)
                    .SelectMany(d => { try { return Directory.EnumerateDirectories(d).Prepend(d); } catch { return new[] { d }; } })
                    .Where(d => d.IndexOf("otzaria", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(d => Path.Combine(d, "shared_preferences.json")).Where(File.Exists);
                foreach (var f in files)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Name.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0 && p.Value.ValueKind == JsonValueKind.True) on = true;
                }
            }
        }
        catch (Exception ex) { Config.Log("offline mode read: " + ex.Message); }
        _cache = on; _at = DateTime.UtcNow;
        return on;
    }
}
