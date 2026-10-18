using System.Text.Json;

namespace FluentScanner;

/// Persisted UI prefs (%LocalAppData%\FluentScanner\settings.json,
/// unpackaged-safe): language override, table font size + mono toggle,
/// theme override. Mirrors macOS SettingsStore defaults.
internal sealed class AppSettings
{
    public string Language { get; set; } = "system"; // system|en|it
    public double FontSize { get; set; } = 12;       // 10..16
    public bool MonoBody { get; set; } = false;
    public string Theme { get; set; } = "system";    // system|light|dark

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentScanner", "settings.json");

    internal static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path));
                if (s is not null)
                {
                    if (s.Language is not ("system" or "en" or "it")) s.Language = "system";
                    if (s.Theme is not ("system" or "light" or "dark")) s.Theme = "system";
                    s.FontSize = Math.Clamp(s.FontSize, 10, 16);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// Effective UI language subtag: settings override wins, else system.
    internal string UiLang()
    {
        if (Language == "en" || Language == "it") return Language;
        var sys = Windows.Globalization.ApplicationLanguages.Languages
            .FirstOrDefault()?.Split('-')[0] ?? "";
        return sys == "en" ? "en" : "it";
    }

    internal Microsoft.UI.Xaml.ElementTheme ThemeEnum() => Theme switch
    {
        "light" => Microsoft.UI.Xaml.ElementTheme.Light,
        "dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
        _ => Microsoft.UI.Xaml.ElementTheme.Default,
    };
}
