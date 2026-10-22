using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace FluentScanner;

public sealed partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// Theme-aware grid-line color (black on light, white on dark),
    /// shared by the header separators.
    private SolidColorBrush _gridLineBrush = new(Windows.UI.Color.FromArgb(255, 0, 0, 0));
    public SolidColorBrush GridLineBrush
    {
        get => _gridLineBrush;
        private set
        {
            _gridLineBrush = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GridLineBrush)));
        }
    }

    private CancellationTokenSource _cts = new();
    private bool _running;
    private readonly ObservableCollection<HostRow> _rows = [];
    private readonly Dictionary<string, HostRow> _byIp = [];
    private AppSettings _settings = AppSettings.Load();

    // Sort/filter state: the column index matches the header Tags (0..6);
    // the default (IP ascending) is the pre-sort behavior. The visible order
    // drives the zebra RowIndex *and* the CSV export, like the macOS table.
    private const int ColHostname = 1, ColMac = 2, ColBrand = 3, ColKind = 4,
        ColServices = 5, ColLatency = 6;
    private int _sortCol;
    private bool _sortAsc = true;
    private string _filter = "";
    // Header tap bookkeeping: the second tap of a double-click sizes the
    // column (and undoes the sort of the first one).
    private int _lastTapCol = -1;
    private int _lastTapTick;
    private int _prevSortCol;
    private bool _prevSortAsc = true;

    // Code-behind strings: read from the .resw files shipped next to the exe
    // (Strings/<language>/). No MRT/ResourceLoader: it needs package identity,
    // missing in unpackaged apps (FileNotFoundException in the constructor).
    // x:Uid in XAML uses internal MRT and works for the system language;
    // the settings override is applied here in code (ApplyLanguage).
    private static Dictionary<string, string>? _res;
    private static string _resLang = "";
    internal string Str(string key)
    {
        try
        {
            var lang = _settings.UiLang();
            if (_res is null || _resLang != lang)
            {
                _res = LoadResw(lang == "en" ? "en-US" : "it-IT");
                _resLang = lang;
            }
            if (_res.TryGetValue(key, out var s)) return s;
        }
        catch (Exception ex) { Debug.WriteLine($"Str({key}): {ex.Message}"); }
        return $"[{key}]";
    }

    private static Dictionary<string, string> LoadResw(string folder) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", folder, "Resources.resw"))
            .Root!.Elements("data")
            .Where(e => e.Attribute("name")?.Value != null && e.Element("value") != null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

    public MainWindow()
    {
        InitializeComponent();

        // Windows 11 Fluent window: Mica backdrop + custom title bar.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(iconPath);
        try { TitleIcon.Source = new BitmapImage(new Uri(iconPath)); } catch { }
        Title = "FluentScanner";

        BuildFontSizeMenu();
        Lv.ItemsSource = _rows;
        if (Scanner.LocalSubnet() is { } sub)
        {
            StartBox.Text = sub.start;
            EndBox.Text = sub.end;
        }
        ApplySettings();
        ApplyLanguage();
        StatusTxt.Text = Str("empty");
        AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1080, Height = 650 });
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } };
    }

    // MARK: Threading — the scan engine runs on a threadpool thread so the
    // window stays responsive (resize, menus) during scans. Row/progress UI
    // may ONLY be touched on the UI thread: DispatchAsync awaits a UI turn,
    // Post fires and forgets (progress/status). All *Async scan helpers
    // below are threadpool-safe (pure network + local state).

    private Task DispatchAsync(Action a)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { a(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
                tcs.TrySetException(new OperationCanceledException());
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        return tcs.Task;
    }

    private Task<T> DispatchAsync<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { tcs.TrySetResult(f()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
                tcs.TrySetException(new OperationCanceledException());
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        return tcs.Task;
    }

    private void Post(Action a)
    {
        try { DispatcherQueue.TryEnqueue(() => { try { a(); } catch { } }); }
        catch { }
    }

    private sealed class ScanCounters { public int Ping; public int Tcp; }

    private void BuildFontSizeMenu()
    {
        for (int size = 10; size <= 16; size++)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = size.ToString(),
                Tag = size,
                GroupName = "SizeGroup",
            };
            item.Click += SizeMenu_Click;
            FontSizeSubItem.Items.Add(item);
        }
    }

    private void ApplySettings()
    {
        _settings.ApplyTuning(); // port lists + timeouts → scan engine
        if (Content is FrameworkElement root) root.RequestedTheme = _settings.ThemeEnum();
        HostRow.DarkMode = EffectiveDark();
        ApplyCaptionColors();
        GridLineBrush = new SolidColorBrush(HostRow.DarkMode
            ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(255, 0x00, 0x00, 0x00));
        foreach (var row in _rows) row.RefreshTheme();
        BuildKindLegend();
        _rowFont = new FontFamily(_settings.MonoBody ? "Consolas" : "Segoe UI");
        foreach (var row in _rows) ApplyFont(row);
    }

    private FontFamily _rowFont = new("Segoe UI");

    private void ApplyFont(HostRow row)
    {
        row.RowFontSize = _settings.FontSize;
        row.RowFontFamily = _rowFont;
    }

    /// Effective dark mode: settings override wins, else the Windows
    /// app-theme registry value (same rule as FluentNotepad).
    private bool EffectiveDark()
    {
        if (_settings.Theme == "dark") return true;
        if (_settings.Theme == "light") return false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { }
        return false;
    }

    /// Windows paints the caption buttons (min/max/close) with the OS app
    /// theme, so a theme override leaves black glyphs on the dark mica title
    /// bar — nearly invisible. Pin the button colours to the theme the app
    /// actually renders (same rule as the rows: override wins, else system).
    private void ApplyCaptionColors()
    {
        var tb = AppWindow.TitleBar;
        bool dark = EffectiveDark();
        var glyph = dark ? Windows.UI.Color.FromArgb(255, 0xE9, 0xE9, 0xE9)
                        : Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
        tb.ButtonForegroundColor = glyph;
        tb.ButtonHoverForegroundColor = glyph;
        tb.ButtonPressedForegroundColor = glyph;
        tb.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
    }

    private void ApplyLanguage()
    {
        _res = null; // force reload for the (possibly new) override
        GetIpBtn.Content = Str("getIp");
        ToolTipService.SetToolTip(GetIpBtn, Str("getIpHelp"));
        StartBtn.Content = _running ? Str("btnStop") : Str("btnStart");
        StopBtn.Content = Str("btnStop");
        ExportBtn.Content = Str("btnExport");
        FileMenu.Title = Str("menuFile");
        ExportMenuItem.Text = Str("fileExport");
        ExitMenuItem.Text = Str("fileExit");
        ScanMenu.Title = Str("menuScan");
        StartMenuItem.Text = _running ? Str("btnStop") : Str("btnStart");
        GetIpMenuItem.Text = Str("getIp");
        ViewMenu.Title = Str("menuView");
        ThemeSubItem.Text = Str("themeLabel");
        ThemeSystemItem.Text = Str("themeSystem");
        ThemeLightItem.Text = Str("themeLight");
        ThemeDarkItem.Text = Str("themeDark");
        LangSubItem.Text = Str("languageLabel");
        LangSystemItem.Text = Str("languageSystem");
        MonoMenuItem.Text = Str("monoLabel");
        FontSizeSubItem.Text = Str("fontSizeLabel");
        HelpMenu.Title = Str("menuHelp");
        HelpMenuItem.Text = Str("helpItem");
        AboutMenuItem.Text = Str("aboutItem");
        SettingsMenuItem.Text = Str("settingsTitle");
        FilterBox.PlaceholderText = Str("searchLabel");
        RefreshHeaders();
        RandomLegendTxt.Text = Str("randomMacLegend");
        SelfLegendTxt.Text = Str("thisPcLegend");
        BuildKindLegend();
        RefreshViewMenu();
        if (!_running && _rows.Count == 0) StatusTxt.Text = Str("empty");
    }

    private void RefreshViewMenu()
    {
        ThemeSystemItem.IsChecked = _settings.Theme == "system";
        ThemeLightItem.IsChecked = _settings.Theme == "light";
        ThemeDarkItem.IsChecked = _settings.Theme == "dark";
        LangSystemItem.IsChecked = _settings.Language == "system";
        LangEnglishItem.IsChecked = _settings.Language == "en";
        LangItalianItem.IsChecked = _settings.Language == "it";
        MonoMenuItem.IsChecked = _settings.MonoBody;
        foreach (var item in FontSizeSubItem.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = item.Tag is int s && s == (int)_settings.FontSize;
        StartMenuItem.Text = _running ? Str("btnStop") : Str("btnStart");
    }

    private void BuildKindLegend()
    {
        KindLegendPanel.Children.Clear();
        // NOTE: resolve from the ACTIVE theme dictionary (HostRow.DarkMode),
        // not from Application.Current.Resources directly: that lookup uses
        // the system theme, so with OS-light + app-dark the labels came out
        // black-on-dark. Rebuilt on every ApplySettings, so it stays correct.
        var secondary = SecondaryBrush();
        foreach (var kind in HostRow.KindNames)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = Brush(HostRow.KindHex(kind)),
            };
            var label = new TextBlock
            {
                Text = kind, FontSize = 11, Foreground = secondary,
            };
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 3,
                Children = { dot, label },
            };
            KindLegendPanel.Children.Add(item);
        }
    }

    private static Brush SecondaryBrush()
    {
        try
        {
            var dicts = Application.Current.Resources.ThemeDictionaries;
            if (dicts.TryGetValue(HostRow.DarkMode ? "Dark" : "Light", out var o) &&
                o is ResourceDictionary d &&
                d.TryGetValue("TextFillColorSecondaryBrush", out var v) && v is Brush b)
                return b;
        }
        catch { }
        return new SolidColorBrush(HostRow.DarkMode
            ? Windows.UI.Color.FromArgb(255, 0xCC, 0xCC, 0xCC)
            : Windows.UI.Color.FromArgb(255, 0x61, 0x61, 0x61));
    }

    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)));
    }

    // MARK: Row plumbing (all on the UI thread — ScanAsync continuations stay
    // on the dispatcher; worker tasks only return values).

    private HostRow RowFor(string ip)
    {
        if (_byIp.TryGetValue(ip, out var row)) return row;
        row = new HostRow(ip);
        ApplyFont(row);
        _byIp[ip] = row;
        // A filter hides non-matching rows; SortRows() reconsiders every row
        // once its hostname/services fill in.
        if (_filter.Length == 0 || MatchesFilter(row)) _rows.Add(row);
        return row;
    }

    private void SortRows()
    {
        // Same HostRow objects are re-added: IsLocal survives rebuilds (0.8.1).
        // RowIndex drives the zebra striping of the visible (filtered) list.
        var list = _byIp.Values.Where(r => _filter.Length == 0 || MatchesFilter(r)).ToList();
        list.Sort(CompareRows);
        _rows.Clear();
        for (int i = 0; i < list.Count; i++)
        {
            list[i].RowIndex = i;
            _rows.Add(list[i]);
        }
    }

    /// Case-insensitive substring over the same fields as the macOS
    /// publishAll (ip, hostname, mac, vendor text, services).
    private bool MatchesFilter(HostRow r)
    {
        bool Hit(string s) => s.Contains(_filter, StringComparison.OrdinalIgnoreCase);
        return Hit(r.Ip) || Hit(r.Hostname) || Hit(r.Mac) || Hit(r.VendorText) || Hit(r.Services);
    }

    /// Active column first, IP as the tie-break so the order stays stable
    /// while rows fill in during the scan.
    private int CompareRows(HostRow a, HostRow b)
    {
        int c = _sortCol switch
        {
            ColHostname => string.Compare(a.Hostname, b.Hostname, StringComparison.OrdinalIgnoreCase),
            ColMac => string.Compare(a.Mac, b.Mac, StringComparison.OrdinalIgnoreCase),
            ColBrand => string.Compare(a.Brand, b.Brand, StringComparison.OrdinalIgnoreCase),
            ColKind => string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase),
            ColServices => string.Compare(a.Services, b.Services, StringComparison.OrdinalIgnoreCase),
            ColLatency => (a.LatencyMs ?? int.MaxValue).CompareTo(b.LatencyMs ?? int.MaxValue),
            _ => Scanner.IpCompare(a.Ip, b.Ip),
        };
        if (c == 0) c = Scanner.IpCompare(a.Ip, b.Ip);
        return _sortAsc ? c : -c;
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        SortRows();
    }

    // MARK: Sortable headers (click a column title to sort, arrow marks the
    // active column; double-click sizes the column to its contents — the
    // macOS table does both the same way).

    private void Header_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag ||
            !int.TryParse(tag, out var col)) return;
        // Remember what sorting looked like: the DoubleTapped that follows in
        // a double-click puts it back (a double-click only sizes the column).
        _prevSortCol = _sortCol;
        _prevSortAsc = _sortAsc;
        _lastTapCol = col;
        _lastTapTick = Environment.TickCount;
        if (col == _sortCol) _sortAsc = !_sortAsc;
        else { _sortCol = col; _sortAsc = true; }
        SortRows();
        RefreshHeaders();
    }

    /// Double-click a header title: size the column to its contents (the
    /// macOS table fits the column under the pointer anywhere on the header
    /// band; Excel fits on the boundary — see `Gripper_DoubleTapped`, which
    /// covers that gesture too). WinUI delivers the first tap of the pair
    /// as a regular Tapped, so undo that sort toggle here — the order must
    /// not move on a double-click.
    private void Header_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag ||
            !int.TryParse(tag, out var col)) return;
        if (col == _lastTapCol && unchecked(Environment.TickCount - _lastTapTick) < 800)
        {
            _sortCol = _prevSortCol;
            _sortAsc = _prevSortAsc;
            SortRows();
            RefreshHeaders();
        }
        AutoFitColumn(col);
        e.Handled = true;
    }

    /// Size the column to its widest cell (header title included).
    /// Measure with a throwaway TextBlock: the ListView virtualizes rows, so
    /// only the realized containers are on screen and measuring them would
    /// size the column to what happens to be visible. The data (all visible
    /// rows) is measured instead, with the same font as the cells.
    private void AutoFitColumn(int col)
    {
        var headers = new[] { HdrIp, HdrHostname, HdrMac, HdrBrand, HdrKind, HdrServices, HdrLatency };
        var header = headers[col];
        double width = Measure(header.Text + "  ", header.FontSize, header.FontFamily, header.FontWeight);
        bool selfIcon = false;
        foreach (var r in _rows)
        {
            double cell = col == ColServices
                ? ServicesWidth(r)
                : Measure(CellText(r, col), r.RowFontSize, r.RowFontFamily, null);
            if (col == 0)
            {
                cell += 14; // device-type dot + gap
                if (r.IsLocal) selfIcon = true;
            }
            width = Math.Max(width, cell);
        }
        if (selfIcon) width += _settings.FontSize + 6; // 🖥 self-host icon + gap
        Cols.Set(col, Math.Min(640, width + (col == ColLatency ? 14 : 20)));
    }

    private double ServicesWidth(HostRow r)
    {
        double w = r.PlainServices.Length == 0
            ? 0
            : Measure(r.PlainServices + (r.Links.Count > 0 ? ", " : ""),
                      r.RowFontSize, r.RowFontFamily, null);
        // HTTP/HTTPS entries are HyperlinkButtons: fixed 12px font, 4px side
        // padding (see the list item template).
        foreach (var link in r.Links)
            w += Measure(link.Label, 12, new FontFamily("Segoe UI"), null) + 12;
        return w;
    }

    private static string CellText(HostRow r, int col) => col switch
    {
        ColHostname => r.Hostname,
        ColMac => r.Mac,
        ColBrand => r.Brand,
        ColKind => r.Kind,
        ColServices => r.Services,
        ColLatency => r.Latency,
        _ => r.Ip,
    };

    private static double Measure(string text, double size, FontFamily family,
        Windows.UI.Text.FontWeight? weight)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = family,
            FontWeight = weight ?? Microsoft.UI.Text.FontWeights.Normal,
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    /// Header titles + the sort arrow (called on language and sort changes).
    private void RefreshHeaders()
    {
        var heads = new (TextBlock T, string Key)[]
        {
            (HdrIp, "thIp"), (HdrHostname, "thHostname"), (HdrMac, "thMac"),
            (HdrBrand, "thBrand"), (HdrKind, "thKind"), (HdrServices, "thServices"),
            (HdrLatency, "thLatency"),
        };
        for (int i = 0; i < heads.Length; i++)
            heads[i].T.Text = Str(heads[i].Key) +
                (i == _sortCol ? (_sortAsc ? " ▲" : " ▼") : "");
    }

    private void ApplyMac(string ip, Dictionary<string, string> arp)
    {
        if (!_byIp.TryGetValue(ip, out var old)) return;
        if (!arp.TryGetValue(ip, out var mac)) return;
        old.Mac = mac;
        old.Vendor = Scanner.Vendor(mac) ?? "";
    }

    private void ApplyKind(string ip, string kind)
    {
        if (!_byIp.TryGetValue(ip, out var old)) return;
        if (!string.IsNullOrEmpty(old.Kind)) return;
        old.Kind = kind;
    }

    /// Merge a hostname keeping the source precedence (3 mDNS > 2 reverse DNS
    /// > 1 NetBIOS). Lookups land chunk by chunk, in whatever order they
    /// answer, so the rank — not the merge order — decides who wins.
    private void SetHostname(string ip, string name, int rank)
    {
        var r = RowFor(ip);
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            name = name[..^".local".Length];
        name = name.TrimEnd('.');
        if (name.Length == 0 || r.NameRank >= rank) return;
        r.Hostname = name;
        r.NameRank = rank;
    }

    private void UpdateStatus(int done, int total) =>
        StatusTxt.Text = string.Format(Str("scanningFmt"), done, total);

    // MARK: Actions

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { Stop_Click(sender, e); return; }
        var s = StartBox.Text.Trim();
        var en = EndBox.Text.Trim();
        var ips = Scanner.Targets(s == en ? s : $"{s}-{en}");
        if (ips.Count == 0)
        {
            StatusTxt.Text = Str("invalidRange");
            return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        StartBtn.Content = Str("btnStop");
        StartMenuItem.Text = Str("btnStop");
        StopBtn.IsEnabled = true;
        ExportBtn.IsEnabled = false;
        _rows.Clear();
        _byIp.Clear();
        Progress.Value = 0;
        Lv.ItemsSource = _rows;
        UpdateStatus(0, ips.Count);

        bool cancelled = false;
        string? error = null;
        try
        {
            // ScanCoreAsync runs on a threadpool thread (UI stays live);
            // every row/progress touch is marshaled via DispatchAsync/Post.
            await Task.Run(() => ScanCoreAsync(ips, _cts.Token), _cts.Token);
            cancelled = _cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            cancelled = false;
            error = ex.Message;
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            StartBtn.Content = Str("btnStart");
            StartMenuItem.Text = Str("btnStart");
            StopBtn.IsEnabled = false;
            ExportBtn.IsEnabled = _rows.Count > 0;
            Progress.Value = 1;
            StatusTxt.Text = error ?? (cancelled
                ? Str("stopped")
                : string.Format(Str("doneFmt"), _rows.Count));
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void GetIp_Click(object sender, RoutedEventArgs e)
    {
        if (Scanner.LocalSubnet() is { } sub)
        {
            StartBox.Text = sub.start;
            EndBox.Text = sub.end;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void ServiceLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as HyperlinkButton)?.DataContext is ServiceLink link &&
            Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
        {
            try { await Windows.System.Launcher.LaunchUriAsync(uri); }
            catch (Exception ex) { Debug.WriteLine($"LaunchUri {link.Url}: {ex.Message}"); }
        }
    }

    private void ThemeMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioMenuFlyoutItem)?.Tag as string is not { } t) return;
        _settings.Theme = t;
        _settings.Save();
        ApplySettings();
        RefreshViewMenu();
    }

    private void LangMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioMenuFlyoutItem)?.Tag as string is not { } l) return;
        _settings.Language = l;
        _settings.Save();
        ApplyLanguage();
    }

    private void MonoMenu_Click(object sender, RoutedEventArgs e)
    {
        _settings.MonoBody = MonoMenuItem.IsChecked;
        _settings.Save();
        ApplySettings();
    }

    private void SizeMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioMenuFlyoutItem)?.Tag is not int s) return;
        _settings.FontSize = s;
        _settings.Save();
        ApplySettings();
        RefreshViewMenu();
    }

    /// Scan tuning dialog (port lists + timeouts): the same intent as the
    /// macOS SettingsView fields, opened from the View menu like the other
    /// former settings-dialog options.
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var stack = new StackPanel { Spacing = 12, Width = 420 };

        var ping = new Slider { Minimum = 500, Maximum = 2000, StepFrequency = 250, Value = _settings.PingTimeoutMs };
        var disc = new Slider { Minimum = 500, Maximum = 3000, StepFrequency = 500, Value = _settings.DiscoveryTimeoutMs };
        var port = new Slider { Minimum = 500, Maximum = 3000, StepFrequency = 500, Value = _settings.PortTimeoutMs };
        BindSlider(ping, "pingTimeoutLabel", v => $"{(int)v} ms");
        BindSlider(disc, "discTimeoutLabel", v => $"{v / 1000.0:0.0} s");
        BindSlider(port, "portTimeoutLabel", v => $"{v / 1000.0:0.0} s");

        var discPorts = new TextBox { Header = Str("discPortsLabel"), Text = _settings.DiscoveryPortsText };
        var scanPorts = new TextBox { Header = Str("scanPortsLabel"), Text = _settings.ScanPortsText };
        var err = new TextBlock
        {
            Text = Str("portsInvalid"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = CriticalBrush(),
            Visibility = Visibility.Collapsed,
        };

        stack.Children.Add(ping);
        stack.Children.Add(disc);
        stack.Children.Add(port);
        stack.Children.Add(discPorts);
        stack.Children.Add(scanPorts);
        stack.Children.Add(err);

        var dlg = new ContentDialog
        {
            Title = Str("settingsTitle"),
            Content = new ScrollViewer
            {
                Content = stack, MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = Str("saveBtn"),
            CloseButtonText = Str("closeBtn"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        // Invalid port text keeps the dialog open (like the macOS inline
        // error) — never let a typo reach the scan engine.
        dlg.PrimaryButtonClick += (_, args) =>
        {
            var dp = Scanner.ParsePorts(discPorts.Text);
            var sp = Scanner.ParsePorts(scanPorts.Text);
            if (dp is null || sp is null)
            {
                err.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
            _settings.PingTimeoutMs = (int)ping.Value;
            _settings.DiscoveryTimeoutMs = (int)disc.Value;
            _settings.PortTimeoutMs = (int)port.Value;
            _settings.DiscoveryPortsText = string.Join(", ", dp);
            _settings.ScanPortsText = string.Join(", ", sp);
            _settings.Save();
            ApplySettings();
        };
        await dlg.ShowAsync();
    }

    /// Slider header doubles as label + live value (no extra text row).
    private void BindSlider(Slider s, string key, Func<double, string> fmt)
    {
        void Update() => s.Header = $"{Str(key)}: {fmt(s.Value)}";
        s.ValueChanged += (_, _) => Update();
        Update();
    }

    /// Theme-aware error red (same resolution rule as SecondaryBrush).
    private static Brush CriticalBrush()
    {
        try
        {
            var dicts = Application.Current.Resources.ThemeDictionaries;
            if (dicts.TryGetValue(HostRow.DarkMode ? "Dark" : "Light", out var o) &&
                o is ResourceDictionary d &&
                d.TryGetValue("SystemFillColorCriticalBrush", out var v) && v is Brush b)
                return b;
        }
        catch { }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
    }

    private void Range_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            Start_Click(sender, e);
            e.Handled = true;
        }
    }

    // MARK: Column resize grippers (WinUI has no DataGrid: header and rows
    // share one ColumnWidths instance, so a drag resizes both together).

    private ColumnWidths Cols => (ColumnWidths)Application.Current.Resources["Cols"];
    private int _gripCol = -1;
    private double _gripLastX;

    /// Excel-style boundary gesture: a double-click on the resize gripper
    /// sizes the column to its contents. The cursor (the west-east resize
    /// arrow) comes from the `CursorGripper` element itself.
    private void Gripper_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag ||
            !int.TryParse(tag, out var col)) return;
        _gripCol = -1;
        if (sender is UIElement u) u.Opacity = 0;
        AutoFitColumn(col);
        e.Handled = true;
    }

    private void Gripper_Enter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement u) u.Opacity = 0.7;
    }

    private void Gripper_Exit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement u && _gripCol < 0) u.Opacity = 0;
    }

    private void Gripper_Press(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag || !int.TryParse(tag, out var col)) return;
        _gripCol = col;
        // Positions come from the header grid, not from the gripper: the
        // gripper rides the column edge, so a relative reading would move
        // with the thing being resized (the drag would not accumulate).
        _gripLastX = e.GetCurrentPoint(HeaderGrid).Position.X;
        el.CapturePointer(e.Pointer);
        el.Opacity = 1;
        e.Handled = true;
    }

    private void Gripper_Move(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_gripCol < 0) return;
        double x = e.GetCurrentPoint(HeaderGrid).Position.X;
        Cols.Set(_gripCol, Cols[_gripCol].Value + (x - _gripLastX));
        _gripLastX = x;
        e.Handled = true;
    }

    private void Gripper_Release(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement el) el.ReleasePointerCapture(e.Pointer);
        if (sender is UIElement u) u.Opacity = 0;
        _gripCol = -1;
        e.Handled = true;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        // Unpackaged app: the picker needs the HWND of the owner window.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedFileName = "FluentScanner.csv";
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var lines = new List<string> { "ip,hostname,mac,vendor,services,latency_ms" };
            foreach (var r in _rows)
                lines.Add(string.Join(",",
                    CsvField(r.Ip), CsvField(r.Hostname), CsvField(r.Mac),
                    CsvField(r.VendorText), CsvField(r.Services),
                    CsvField(r.LatencyMs?.ToString() ?? "")));
            await Windows.Storage.FileIO.WriteTextAsync(file, string.Join("\r\n", lines));
            StatusTxt.Text = Str("exported");
        }
        catch
        {
            StatusTxt.Text = Str("exportedFailed");
        }
    }

    private static string CsvField(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Contains(',') || s.Contains('"')
            ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = Str("helpTitle"),
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = Str("helpBody"), TextWrapping = TextWrapping.Wrap },
                Height = 380, Width = 480,
            },
            PrimaryButtonText = Str("closeBtn"),
            XamlRoot = Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var v = GetType().Assembly.GetName().Version?.ToString(3) ?? "";
        var stack = new StackPanel { Spacing = 8, Width = 400 };
        stack.Children.Add(new TextBlock
        {
            Text = $"FluentScanner {v}",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        foreach (var key in new[] { "aboutDesc", "aboutOui", "aboutLicense" })
            stack.Children.Add(new TextBlock
            {
                Text = Str(key), TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        var dlg = new ContentDialog
        {
            Title = Str("aboutTitle"),
            Content = stack,
            PrimaryButtonText = Str("closeBtn"),
            XamlRoot = Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // MARK: Scan flow (threadpool) — discovery → ARP + port sweep → reverse
    // DNS. Rows update live; every UI touch goes through DispatchAsync/Post.

    private async Task ScanCoreAsync(List<uint> ips, CancellationToken ct)
    {
        int total = ips.Count;
        var arp = Scanner.ArpTable(); // snapshot: devices seen before keep MACs

        // The scanning host always shows, highlighted — even if it drops
        // its own ping: local IP + MAC come from the interfaces.
        var locals = Scanner.LocalAddrs();
        await DispatchAsync(() =>
        {
            foreach (var (ip, mac) in locals)
            {
                var r = RowFor(ip);
                r.IsLocal = true;
                if (string.IsNullOrEmpty(r.Mac) && mac is not null)
                {
                    r.Mac = mac;
                    r.Vendor = Scanner.Vendor(mac) ?? "";
                }
            }
        });

        // Phase 1 — ping sweep + TCP discovery run CONCURRENTLY (wall time ≈
        // the slower pass, not the sum — macOS 0.7.0 mirror).
        var dotted = ips.Select(Scanner.Dotted).ToList();
        var counters = new ScanCounters();
        void Report()
        {
            int p = Volatile.Read(ref counters.Ping), t = Volatile.Read(ref counters.Tcp);
            int done = Math.Min(p + t, 2 * total);
            Post(() =>
            {
                Progress.Value = (double)done / Math.Max(1, 2 * total);
                StatusTxt.Text = string.Format(Str("scanningFmt"), Math.Min(done, total), total);
            });
        }
        Report();
        var pingTask = SweepPingAsync(dotted, total, ct, counters, Report);
        var tcpTask = SweepTcpAsync(dotted, total, ct, counters, Report);
        await Task.WhenAll(pingTask, tcpTask);
        ct.ThrowIfCancellationRequested();
        var pingHits = await pingTask;
        var tcpHits = await tcpTask;
        // Both passes populate the ARP table → one refresh for all MACs. The
        // rows and their latency already landed chunk by chunk in the sweeps.
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ip, _, _) in pingHits) alive.Add(ip);
        foreach (var (ip, _) in tcpHits) alive.Add(ip);
        foreach (var kv in Scanner.ArpTable()) arp[kv.Key] = kv.Value;
        await DispatchAsync(() =>
        {
            foreach (var ip in alive) ApplyMac(ip, arp);
            SortRows();
        });
        Report();
        if (alive.Count == 0 || ct.IsCancellationRequested) return;
        Post(() => StatusTxt.Text = string.Format(Str("portScanFmt"), alive.Count));

        // Responders with still-missing ARP entries (expired/raced): targeted
        // re-ping repopulates them, then one more ARP re-read (macOS mirror).
        var missing = alive.Where(ip => !arp.ContainsKey(ip)).ToList();
        for (int c = 0; c < missing.Count; c += Scanner.PingChunk)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = missing.Skip(c).Take(Scanner.PingChunk).ToList();
            await Task.WhenAll(chunk.Select(async ip =>
            {
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    await ping.SendPingAsync(ip, Scanner.PingTimeoutMs);
                }
                catch { }
            }));
        }
        if (missing.Count > 0)
        {
            foreach (var kv in Scanner.ArpTable()) arp[kv.Key] = kv.Value;
            await DispatchAsync(() =>
            {
                foreach (var ip in alive) ApplyMac(ip, arp);
                SortRows();
            });
        }

        // Phase 2 — port sweep on live hosts, pairs chunked to bound sockets
        var pairs = alive
            .SelectMany(ip => Scanner.ScanPorts.Select(p => (ip, p)))
            .OrderBy(t => t.ip, StringComparer.Ordinal)
            .ToList();
        for (int c = 0; c < pairs.Count; c += Scanner.PortChunk)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pairs.Skip(c).Take(Scanner.PortChunk).ToList();
            var opens = await Task.WhenAll(chunk.Select(async t =>
                await Scanner.ProbeAsync(t.ip, t.p, Scanner.PortTimeoutMs, ct) is not null
                    ? (ip: t.ip, p: t.p) : (ip: t.ip, p: 0)));
            var found = opens.Where(o => o.p != 0).ToList();
            if (found.Count > 0)
                await DispatchAsync(() =>
                {
                    foreach (var (ip, p) in found) RowFor(ip).AddPort(p);
                });
        }
        await DispatchAsync(SortRows);

        // Vendor + kind from ports/Vendor (OUI); the TTL hint from the ping
        // only survives when classification found nothing better.
        await DispatchAsync(() =>
        {
            foreach (var ip in _byIp.Keys.ToList())
            {
                var row = _byIp[ip];
                row.Kind = Scanner.Classify(
                    string.IsNullOrEmpty(row.Vendor) ? null : row.Vendor,
                    new HashSet<int>(row.Ports)) ?? row.Kind;
            }
            SortRows();
        });

        // Phase 3 — hostname + software lookups run CONCURRENTLY, and every
        // chunk is merged the moment it answers: names fill in progressively,
        // so a slow source (reverse DNS with no reachable resolver, e.g. an
        // AP with no internet) never holds the hosts already found hostage.
        // Precedence stays mDNS → reverse DNS → NetBIOS (ranked, arrival order
        // irrelevant); the HTTP Server header fills the brand slot only when
        // OUI is silent.
        Post(() => StatusTxt.Text = Str("dnsFmt"));
        await DispatchAsync(SortRows);
        var snap = alive.ToList();
        var httpPairs = await DispatchAsync(() => _byIp.Values
            .Where(r => r.Ports.Contains(80) || r.Ports.Contains(8080))
            .Select(r => (r.Ip, port: r.Ports.Contains(80) ? 80 : 8080))
            .ToList());
        var mdnsTask = Scanner.MdnsHarvestAsync(2.5, ct);
        var dnsTask = ReverseAllAsync(snap, ct);      // merges per chunk
        var nbnsTask = NbnsAllAsync(snap, ct);        // merges per chunk
        var httpTask = HttpAllAsync(httpPairs, ct);   // merges per chunk
        await Task.WhenAll(mdnsTask, dnsTask, nbnsTask, httpTask);
        ct.ThrowIfCancellationRequested();
        var mdnsHosts = await mdnsTask;
        await DispatchAsync(() =>
        {
            foreach (var (ip, name) in mdnsHosts) SetHostname(ip, name, 3);
            SortRows();
        });
    }

    /// Bulk ICMP pass: hits carry latency + TTL. Threadpool-safe: progress
    /// is reported through the callback (marshaled by the caller).
    private async Task<List<(string ip, int ms, int? ttl)>> SweepPingAsync(
        List<string> dotted, int total, CancellationToken ct, ScanCounters counters, Action report)
    {
        var hits = new List<(string, int, int?)>();
        for (int c = 0; c < dotted.Count && !ct.IsCancellationRequested; c += Scanner.PingChunk)
        {
            var chunk = dotted.Skip(c).Take(Scanner.PingChunk).ToList();
            var results = await Task.WhenAll(chunk.Select(async ip =>
            {
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync(ip, Scanner.PingTimeoutMs);
                    var ok = reply.Status == System.Net.NetworkInformation.IPStatus.Success;
                    return (ip: ip, ms: ok ? (int)reply.RoundtripTime : 0,
                        ttl: ok ? reply.Options?.Ttl : null);
                }
                catch { return (ip: ip, ms: 0, ttl: (int?)null); }
            }));
            var found = results.Where(r => r.ms > 0).ToList();
            hits.AddRange(found);
            // Partial results: each chunk's answers are on screen before the
            // next one starts, so the first hosts show up while the sweep is
            // still running (MACs fill in after the ARP re-read).
            await DispatchAsync(() =>
            {
                foreach (var (ip, ms, ttl) in found)
                {
                    var r = RowFor(ip);
                    if (r.LatencyMs is null) r.LatencyMs = ms;
                    if (ttl is { } t && Scanner.TtlKind(t) is { } hint) ApplyKind(ip, hint);
                }
                SortRows();
            });
            Interlocked.Add(ref counters.Ping, chunk.Count);
            report();
        }
        return hits;
    }

    /// Full TCP discovery pass over every target (union with ping).
    private async Task<List<(string ip, int ms)>> SweepTcpAsync(
        List<string> dotted, int total, CancellationToken ct, ScanCounters counters, Action report)
    {
        var hits = new List<(string, int)>();
        var pairs = dotted.SelectMany(ip => Scanner.DiscoveryPorts.Select(p => (ip, p))).ToList();
        for (int c = 0; c < pairs.Count && !ct.IsCancellationRequested; c += Scanner.PortChunk)
        {
            var chunk = pairs.Skip(c).Take(Scanner.PortChunk).ToList();
            var results = await Task.WhenAll(chunk.Select(async t =>
            {
                var ms = await Scanner.ProbeAsync(t.ip, t.p, Scanner.DiscoveryTimeoutMs, ct);
                return (ip: t.ip, ms: ms);
            }));
            var found = new List<(string ip, int ms)>();
            foreach (var r in results)
                if (r.ms is { } m) found.Add((r.ip, m));
            foreach (var (ip, ms) in found) hits.Add((ip, ms));
            // Same live merge as the ping pass: an open discovery port is a
            // row the moment the connect succeeds.
            await DispatchAsync(() =>
            {
                foreach (var (ip, ms) in found)
                {
                    var r = RowFor(ip);
                    if (r.LatencyMs is null) r.LatencyMs = ms;
                }
                SortRows();
            });
            Interlocked.Add(ref counters.Tcp, chunk.Count / Scanner.DiscoveryPorts.Length);
            report();
        }
        return hits.DistinctBy(h => h.Item1).ToList();
    }

    /// Reverse DNS, chunked and merged chunk by chunk (ReverseDnsAsync has a
    /// 1.5 s cap, so an unreachable resolver can never stall the phase).
    private async Task ReverseAllAsync(List<string> hosts, CancellationToken ct)
    {
        for (int c = 0; c < hosts.Count && !ct.IsCancellationRequested; c += 32)
        {
            var chunk = hosts.Skip(c).Take(32).ToList();
            var names = await Task.WhenAll(chunk.Select(async ip =>
                (ip, name: await Scanner.ReverseDnsAsync(ip, ct))));
            await DispatchAsync(() =>
            {
                foreach (var (ip, name) in names)
                    if (name is not null) SetHostname(ip, name, 2);
                SortRows();
            });
        }
    }

    /// NetBIOS node-status, chunked, merged as each chunk answers.
    private async Task NbnsAllAsync(List<string> hosts, CancellationToken ct)
    {
        for (int c = 0; c < hosts.Count && !ct.IsCancellationRequested; c += 32)
        {
            var chunk = hosts.Skip(c).Take(32).ToList();
            var names = await Task.WhenAll(chunk.Select(async ip =>
                (ip, name: await Scanner.NbnsNameAsync(ip, ct))));
            await DispatchAsync(() =>
            {
                foreach (var (ip, name) in names)
                    if (name is not null) SetHostname(ip, name, 1);
                SortRows();
            });
        }
    }

    /// HTTP Server headers for 80/8080 hosts (only already-open ports).
    private async Task HttpAllAsync(List<(string ip, int port)> httpPairs, CancellationToken ct)
    {
        for (int c = 0; c < httpPairs.Count && !ct.IsCancellationRequested; c += 16)
        {
            var chunk = httpPairs.Skip(c).Take(16).ToList();
            var srvs = await Task.WhenAll(chunk.Select(async t =>
                (t.ip, srv: await Scanner.HttpServerAsync(t.ip, t.port, ct))));
            await DispatchAsync(() =>
            {
                foreach (var (ip, srv) in srvs)
                    if (srv is not null)
                    {
                        _byIp[ip].Server = srv;
                        _byIp[ip].RefreshServices();
                    }
                SortRows();
            });
        }
    }
}
