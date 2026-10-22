using System.Text.Json;

namespace FluentScanner;

/// Persisted UI prefs (%LocalAppData%\FluentScanner\settings.json,
/// unpackaged-safe): language override, table font size + mono toggle,
/// theme override, scan tuning (port lists + timeouts). Mirrors the
/// macOS SettingsStore defaults.
internal sealed class AppSettings
{
    public string Language { get; set; } = "system"; // system|en|it
    public double FontSize { get; set; } = 12;       // 10..16
    public bool MonoBody { get; set; } = false;
    public string Theme { get; set; } = "system";    // system|light|dark

    // Scan tuning: fixed working defaults, editable in the Settings dialog
    // (same intent as the macOS SettingsView sliders + port fields).
    public int PingTimeoutMs { get; set; } = 1000;      // 500..2000
    public int DiscoveryTimeoutMs { get; set; } = 2000; // 500..3000
    public int PortTimeoutMs { get; set; } = 1500;      // 500..3000
    public string DiscoveryPortsText { get; set; } = PortsText(Scanner.DefaultDiscoveryPorts);
    public string ScanPortsText { get; set; } = PortsText(Scanner.DefaultScanPorts);

    private static string PortsText(int[] ports) => string.Join(", ", ports);

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
                    s.PingTimeoutMs = Math.Clamp(s.PingTimeoutMs, 500, 2000);
                    s.DiscoveryTimeoutMs = Math.Clamp(s.DiscoveryTimeoutMs, 500, 3000);
                    s.PortTimeoutMs = Math.Clamp(s.PortTimeoutMs, 500, 3000);
                    // Broken/empty port lists fall back to the built-in ones
                    // (never let a hand-edited settings.json kill the scan).
                    if (Scanner.ParsePorts(s.DiscoveryPortsText) is null)
                        s.DiscoveryPortsText = PortsText(Scanner.DefaultDiscoveryPorts);
                    if (Scanner.ParsePorts(s.ScanPortsText) is null)
                        s.ScanPortsText = PortsText(Scanner.DefaultScanPorts);
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

    /// Push the scan tuning into the engine (the macOS side reads the same
    /// values live from UserDefaults).
    internal void ApplyTuning()
    {
        Scanner.PingTimeoutMs = (int)Math.Clamp(PingTimeoutMs, 500, 2000);
        Scanner.DiscoveryTimeoutMs = (int)Math.Clamp(DiscoveryTimeoutMs, 500, 3000);
        Scanner.PortTimeoutMs = (int)Math.Clamp(PortTimeoutMs, 500, 3000);
        if (Scanner.ParsePorts(DiscoveryPortsText) is { } discovery)
            Scanner.DiscoveryPorts = discovery;
        if (Scanner.ParsePorts(ScanPortsText) is { } scan)
            Scanner.ScanPorts = scan;
    }

    /// Effective UI language subtag: settings override wins, else system.
    internal string UiLang()
    {
        if (Language == "en" || Language == "it") return Language;
        var sys = Windows.Globalization.ApplicationLanguages.Languages
            .FirstOrDefault()?.Split('-')[0] ?? "";
        return sys == "it" ? "it" : "en";
    }

    internal Microsoft.UI.Xaml.ElementTheme ThemeEnum() => Theme switch
    {
        "light" => Microsoft.UI.Xaml.ElementTheme.Light,
        "dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
        _ => Microsoft.UI.Xaml.ElementTheme.Default,
    };
}
