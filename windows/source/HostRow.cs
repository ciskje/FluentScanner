using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentScanner;

/// One result row (ListView item; properties raise changes for x:Bind OneWay).
public sealed class HostRow : INotifyPropertyChanged
{
    private string _ip = "";
    public string Ip
    {
        get => _ip;
        set
        {
            if (_ip == value) return;
            _ip = value;
            OnChanged();
        }
    }

    private string _hostname = "";
    // Source precedence of the hostname, kept even when the lookups land out
    // of order: 3 mDNS > 2 reverse DNS > 1 NetBIOS.
    private int _nameRank;
    public int NameRank { get => _nameRank; set => _nameRank = value; }
    private string _mac = "";
    private string _vendor = "";
    private string _kind = "";
    private string _server = "";
    private string _services = "";
    private string _latency = "";
    private int? _latencyMs;
    private bool _isLocal;
    private readonly SortedSet<int> _ports = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public HostRow(string ip, string latency = "")
    {
        _ip = ip;
        _latency = latency;
    }

    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }
    public string Mac
    {
        get => _mac;
        set
        {
            if (Set(ref _mac, value))
            {
                OnChanged(nameof(IsRandomized));
                OnChanged(nameof(RandomVisibility));
                OnChanged(nameof(DotBrush));
            }
        }
    }
    public string Vendor
    {
        get => _vendor;
        set
        {
            if (Set(ref _vendor, value))
            {
                OnChanged(nameof(Brand));
                OnChanged(nameof(BrandBrush));
                OnChanged(nameof(VendorText));
            }
        }
    }
    public string Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                OnChanged(nameof(VendorText));
                OnChanged(nameof(DotBrush));
            }
        }
    }
    public string Server
    {
        get => _server;
        set
        {
            if (Set(ref _server, value))
            {
                OnChanged(nameof(Brand));
                OnChanged(nameof(BrandBrush));
                OnChanged(nameof(VendorText));
            }
        }
    }
    public string Services { get => _services; set => Set(ref _services, value); }

    private string _plainServices = "";
    public string PlainServices { get => _plainServices; private set => Set(ref _plainServices, value); }

    private List<ServiceLink> _links = [];
    public List<ServiceLink> Links { get => _links; private set => Set(ref _links, value); }

    public Microsoft.UI.Xaml.Visibility HasPlainServices =>
        string.IsNullOrEmpty(_plainServices)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
    public string Latency { get => _latency; set => Set(ref _latency, value); }
    public int? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (Set(ref _latencyMs, value))
                Latency = value is { } ms ? $"{ms} ms" : "";
        }
    }

    private double _rowFontSize = 12;
    public double RowFontSize { get => _rowFontSize; set => Set(ref _rowFontSize, value); }

    private Microsoft.UI.Xaml.Media.FontFamily _rowFontFamily = new("Segoe UI");
    public Microsoft.UI.Xaml.Media.FontFamily RowFontFamily
    {
        get => _rowFontFamily;
        set => Set(ref _rowFontFamily, value);
    }
    public bool IsLocal
    {
        get => _isLocal;
        set
        {
            if (Set(ref _isLocal, value))
            {
                OnChanged(nameof(IpForeground));
                OnChanged(nameof(RowFontWeight));
                OnChanged(nameof(LocalVisibility));
            }
        }
    }

    public IReadOnlySet<int> Ports => _ports;

    public void AddPort(int p)
    {
        if (_ports.Add(p)) RefreshServices();
    }

    /// HTTP software name fills the cell when no OUI vendor exists.
    public string Brand => !string.IsNullOrEmpty(_vendor) ? _vendor
        : !string.IsNullOrEmpty(_server) ? _server : "";

    /// Combined "Vendor — Type" cell (list column and CSV share it).
    public string VendorText => string.Join(" — ", new[] { Brand, _kind }
        .Where(s => !string.IsNullOrEmpty(s)));

    public bool IsRandomized => !string.IsNullOrEmpty(_mac) && Scanner.IsRandomized(_mac);

    public Microsoft.UI.Xaml.Visibility LocalVisibility =>
        _isLocal ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility RandomVisibility =>
        IsRandomized ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public void RefreshServices()
    {
        var plain = new List<string>();
        var links = new List<ServiceLink>();
        foreach (var port in _ports.OrderBy(p => p))
        {
            Scanner.PortNames.TryGetValue(port, out var name);
            bool isWeb = name == "HTTP" || name == "HTTPS";
            var disp = name ?? port.ToString();
            if (name == "HTTP" && !string.IsNullOrEmpty(_server))
                disp = $"HTTP/{_server}";
            var text = $"{disp} ({port})";
            if (!isWeb)
            {
                plain.Add(text);
                continue;
            }
            var scheme = name == "HTTPS" ? "https" : "http";
            links.Add(new ServiceLink(text, $"{scheme}://{Ip}:{port}"));
        }
        Services = string.Join(", ", plain.Concat(links.Select(l => l.Label)));
        PlainServices = string.Join(", ", plain);
        Links = links;
        OnChanged(nameof(HasPlainServices));
    }

    // MARK: Presentation (device dot, brand colors, self highlight)

    // Two color sets: pure/bright hues vanish on one of the themes, so
    // every custom color has a light and a dark variant (see windows/AGENTS.md).
    private static readonly (string kind, string hex)[] KindColorsLight =
    [
        ("Printer", "#E67E22"), ("NAS", "#9B59B6"), ("Router/AP", "#27AE60"),
        ("Windows host", "#2980B9"), ("Raspberry Pi", "#E84393"),
        ("Apple device", "#7F8C8D"), ("IoT device", "#B7950B"), ("PC", "#16A9A9"),
    ];

    private static readonly (string kind, string hex)[] KindColorsDark =
    [
        ("Printer", "#F5B041"), ("NAS", "#BB6BD9"), ("Router/AP", "#58D68D"),
        ("Windows host", "#5DADE2"), ("Raspberry Pi", "#F194B8"),
        ("Apple device", "#BDC3C7"), ("IoT device", "#F7DC6F"), ("PC", "#48C9B0"),
    ];

    private static readonly string[] BrandPaletteLight =
    [
        "#2980B9", "#1E8449", "#CA6F1E", "#7D3C98", "#B03A5B",
        "#A93226", "#148F77", "#5D6D7E", "#795548", "#00838F",
    ];

    private static readonly string[] BrandPaletteDark =
    [
        "#5DADE2", "#58D68D", "#F5B041", "#BB6BD9", "#F194B8",
        "#EC7063", "#48C9B0", "#AEB6BF", "#A98274", "#4DD0E1",
    ];

    internal static readonly string[] KindNames =
    [
        "Printer", "NAS", "Router/AP", "Windows host", "Raspberry Pi",
        "Apple device", "IoT device", "PC",
    ];

    internal static string KindHex(string kind)
    {
        var table = DarkMode ? KindColorsDark : KindColorsLight;
        foreach (var (k, hex) in table)
            if (kind == k) return hex;
        return "#808080";
    }

    public SolidColorBrush DotBrush
    {
        get
        {
            var k = _kind ?? "";
            foreach (var name in KindNames)
                if (k.Contains(name, StringComparison.Ordinal))
                    return Brush(KindHex(name));
            return new SolidColorBrush(Colors.Transparent);
        }
    }

    public SolidColorBrush BrandBrush =>
        string.IsNullOrEmpty(Brand) ? new SolidColorBrush(Colors.Transparent) : Brush(BrandHex(Brand));

    public SolidColorBrush IpForeground =>
        _isLocal ? new SolidColorBrush(DarkMode
            ? Colors.Yellow
            : Windows.UI.Color.FromArgb(255, 0x8A, 0x6D, 0x00))
        // Never null: a null Foreground in a compiled binding leaves the
        // text invisible on recycled (virtualized) rows. Resolve the theme's
        // primary text brush explicitly instead of inheriting it.
        : TextPrimaryBrush();

    internal static SolidColorBrush TextPrimaryBrush()
    {
        try
        {
            var dicts = Application.Current.Resources.ThemeDictionaries;
            if (dicts.TryGetValue(DarkMode ? "Dark" : "Light", out var o) &&
                o is ResourceDictionary d &&
                d.TryGetValue("TextFillColorPrimaryBrush", out var v) && v is SolidColorBrush b)
                return b;
        }
        catch { }
        return new SolidColorBrush(DarkMode
            ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(255, 0x1B, 0x1B, 0x1B));
    }

    public SolidColorBrush RowSeparator => new(DarkMode
        ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
        : Windows.UI.Color.FromArgb(255, 0x00, 0x00, 0x00));

    private int _rowIndex;
    public int RowIndex
    {
        get => _rowIndex;
        set
        {
            if (Set(ref _rowIndex, value)) OnChanged(nameof(RowBackground));
        }
    }

    /// Zebra striping, theme-aware (transparent even rows on dark so Mica
    /// shows through).
    public SolidColorBrush RowBackground => new(DarkMode
        ? (_rowIndex % 2 == 0
            ? Windows.UI.Color.FromArgb(0, 0, 0, 0)
            : Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
        : (_rowIndex % 2 == 0
            ? Colors.White
            : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3)));

    /// Set by the window from the effective theme (settings override, else
    /// the OS app theme): the self highlight stays readable on both.
    internal static bool DarkMode { get; set; }

    public void RefreshTheme()
    {
        OnChanged(nameof(IpForeground));
        OnChanged(nameof(DotBrush));
        OnChanged(nameof(BrandBrush));
        OnChanged(nameof(RowBackground));
        OnChanged(nameof(RowSeparator));
    }

    /// Bold whole row for the scanning host.
    public Windows.UI.Text.FontWeight RowFontWeight =>
        _isLocal ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    private static string BrandHex(string brand)
    {
        uint h = 0;
        foreach (var c in brand) h = h * 31 + c;
        var palette = DarkMode ? BrandPaletteDark : BrandPaletteLight;
        return palette[h % (uint)palette.Length];
    }

    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex[2..4], 16);
        byte b = Convert.ToByte(hex[4..6], 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// One clickable HTTP(S) service: label shown on the button, URL opened in
/// the default browser (Launcher.LaunchUriAsync from the Click handler).
public sealed record ServiceLink(string Label, string Url);
