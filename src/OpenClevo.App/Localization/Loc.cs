using System.Text.Json;
using Microsoft.Win32;

namespace OpenClevo.Localization;

/// <summary>
/// UI text lookup. Strings live in <c>langs/&lt;code&gt;.json</c> at the repository root (embedded at build time as
/// <c>langs/&lt;code&gt;.json</c> resources). A key missing in the chosen language falls back to English, then to the key itself.
/// </summary>
public static class Loc
{
    /// <summary>Supported language codes. A new language is a new <c>langs/&lt;code&gt;.json</c> file plus an entry here.</summary>
    public static readonly string[] Languages = ["tr", "en", "de", "fr", "es", "ru", "zh", "ja", "ar"];

    const string Fallback = "en";
    const string AppKey = @"Software\OpenClevo";

    static Dictionary<string, string> _english = new();
    static Dictionary<string, string> _current = new();

    /// <summary>The language in use right now: "tr" or "en".</summary>
    public static string Current { get; private set; } = Fallback;

    /// <summary>The language chosen in the installer or settings: "tr" or "en"; null if none was saved yet.</summary>
    public static string? SavedLanguage
    {
        get
        {
            try
            {
                var v = Registry.CurrentUser.OpenSubKey(AppKey)?.GetValue("Language") as string;
                return Languages.Contains(v) ? v : null;
            }
            catch { return null; }
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(AppKey);
            if (value is null) k.DeleteValue("Language", throwOnMissingValue: false);
            else k.SetValue("Language", value);
        }
    }

    /// <summary>Loads the saved language (English if none is saved). Call once, before any window is created.</summary>
    public static void Init()
    {
        _english = Load(Fallback);
        Set(SavedLanguage ?? Fallback);
    }

    /// <summary>Switches the texts to <paramref name="lang"/> for windows created from now on (nothing is saved).</summary>
    public static void Set(string lang)
    {
        Current = Languages.Contains(lang) ? lang : Fallback;
        _current = Current == Fallback ? _english : Load(Current);
    }

    /// <summary>A language's name in that language, as shown in the picker (never translated).</summary>
    public static string NativeName(string code) => code switch
    {
        "tr" => "Türkçe", "en" => "English", "de" => "Deutsch", "fr" => "Français", "es" => "Español",
        "ru" => "Русский", "zh" => "中文", "ja" => "日本語", "ar" => "العربية", _ => code,
    };

    /// <summary>Arabic is written right to left: windows mirror their layout when it is the current language.</summary>
    public static System.Windows.FlowDirection Flow => Current == "ar" ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;

    /// <summary>Text for a key in the current language.</summary>
    public static string T(string key) =>
        _current.TryGetValue(key, out var v) ? v : _english.TryGetValue(key, out var e) ? e : key;

    /// <summary>Text for a key with {0}, {1}… placeholders filled in.</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    static Dictionary<string, string> Load(string lang)
    {
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream($"langs/{lang}.json");
            return s == null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch { return new(); }
    }
}
