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
        if (Content is FrameworkElement root) root.RequestedTheme = _settings.ThemeEnum();
        HostRow.DarkMode = EffectiveDark();
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
        HdrIp.Text = Str("thIp");
        HdrHostname.Text = Str("thHostname");
        HdrMac.Text = Str("thMac");
        HdrBrand.Text = Str("thBrand");
        HdrKind.Text = Str("thKind");
        HdrServices.Text = Str("thServices");
        HdrLatency.Text = Str("thLatency");
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
        _rows.Add(row);
        return row;
    }

    private void SortRows()
    {
        // Same HostRow objects are re-added: IsLocal survives rebuilds (0.8.1).
        // RowIndex drives the zebra striping.
        var sorted = _byIp.Keys.OrderBy(ip => ip, Comparer<string>.Create(Scanner.IpCompare)).ToList();
        _rows.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            _byIp[sorted[i]].RowIndex = i;
            _rows.Add(_byIp[sorted[i]]);
        }
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

    /// Standard resize cursor (WinUI 3 has no per-control Cursor property:
    /// CoreWindow.PointerCursor is the way; guarded — without it the accent
    /// highlight alone remains as affordance).
    private static void SetResizeCursor(bool on)
    {
        try
        {
            var core = Window.Current?.CoreWindow;
            if (core is null) return;
            core.PointerCursor = new Windows.UI.Core.CoreCursor(
                on ? Windows.UI.Core.CoreCursorType.SizeWestEast
                   : Windows.UI.Core.CoreCursorType.Arrow, 1);
        }
        catch { }
    }

    private void Gripper_Enter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement u)
        {
            u.Opacity = 0.7;
            SetResizeCursor(true);
        }
    }

    private void Gripper_Exit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement u && _gripCol < 0)
        {
            u.Opacity = 0;
            SetResizeCursor(false);
        }
    }

    private void Gripper_Press(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag || !int.TryParse(tag, out var col)) return;
        _gripCol = col;
        _gripLastX = e.GetCurrentPoint(el).Position.X;
        el.CapturePointer(e.Pointer);
        el.Opacity = 1;
        e.Handled = true;
    }

    private void Gripper_Move(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_gripCol < 0 || sender is not FrameworkElement el) return;
        double x = e.GetCurrentPoint(el).Position.X;
        Cols.Set(_gripCol, Cols[_gripCol].Value + (x - _gripLastX));
        _gripLastX = x;
        e.Handled = true;
    }

    private void Gripper_Release(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement el) el.ReleasePointerCapture(e.Pointer);
        if (sender is UIElement u) u.Opacity = 0;
        SetResizeCursor(false);
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
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ip, _, _) in pingHits) alive.Add(ip);
        foreach (var (ip, _) in tcpHits) alive.Add(ip);
        await DispatchAsync(() =>
        {
            foreach (var (ip, ms, ttl) in pingHits)
            {
                var r = RowFor(ip);
                if (r.LatencyMs is null) r.LatencyMs = ms;
                if (ttl is { } t && Scanner.TtlKind(t) is { } hint) ApplyKind(ip, hint);
            }
            foreach (var (ip, ms) in tcpHits)
            {
                var r = RowFor(ip);
                if (r.LatencyMs is null) r.LatencyMs = ms;
            }
        });
        // Both passes populate the ARP table → one refresh for all MACs.
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

        // Phase 3 — hostname + software lookups run CONCURRENTLY (wall time ≈
        // the slowest, not the sum). Precedence: mDNS → reverse DNS → NetBIOS;
        // the HTTP Server header fills the brand slot only when OUI is silent.
        Post(() => StatusTxt.Text = Str("dnsFmt"));
        await DispatchAsync(SortRows);
        var snap = alive.ToList();
        var httpPairs = await DispatchAsync(() => _byIp.Values
            .Where(r => r.Ports.Contains(80) || r.Ports.Contains(8080))
            .Select(r => (r.Ip, port: r.Ports.Contains(80) ? 80 : 8080))
            .ToList());
        var mdnsTask = Scanner.MdnsHarvestAsync(2.5, ct);
        var dnsTask = ReverseAllAsync(snap, ct);
        var nbnsTask = NbnsAllAsync(snap, ct);
        var httpTask = HttpAllAsync(httpPairs, ct);
        await Task.WhenAll(mdnsTask, dnsTask, nbnsTask, httpTask);
        ct.ThrowIfCancellationRequested();
        var mdnsHosts = await mdnsTask;
        var dnsHosts = await dnsTask;
        var nbnsHosts = await nbnsTask;
        var httpHosts = await httpTask;
        await DispatchAsync(() =>
        {
            foreach (var (ip, name) in mdnsHosts)
            {
                if (!_byIp.TryGetValue(ip, out var row) || !string.IsNullOrEmpty(row.Hostname)) continue;
                var baseName = name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                    ? name[..^".local".Length] : name;
                row.Hostname = baseName.TrimEnd('.');
            }
            foreach (var (ip, name) in dnsHosts)
                if (_byIp.TryGetValue(ip, out var row) && string.IsNullOrEmpty(row.Hostname))
                    row.Hostname = name;
            foreach (var (ip, name) in nbnsHosts)
                if (_byIp.TryGetValue(ip, out var row) && string.IsNullOrEmpty(row.Hostname))
                    row.Hostname = name;
            foreach (var (ip, srv) in httpHosts)
                if (_byIp.TryGetValue(ip, out var row))
                    row.Server = srv;
            foreach (var ip in _byIp.Keys) _byIp[ip].RefreshServices();
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
                    return (ip, ok ? (int)reply.RoundtripTime : 0,
                        ok ? reply.Options?.Ttl : null);
                }
                catch { return (ip, 0, (int?)null); }
            }));
            foreach (var (ip, ms, ttl) in results)
                if (ms > 0) hits.Add((ip, ms, ttl));
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
                return (t.ip, ms);
            }));
            foreach (var (ip, ms) in results)
                if (ms is { } m) hits.Add((ip, m));
            Interlocked.Add(ref counters.Tcp, chunk.Count / Scanner.DiscoveryPorts.Length);
            report();
        }
        return hits.DistinctBy(h => h.Item1).ToList();
    }

    private static async Task<List<(string, string)>> ReverseAllAsync(List<string> hosts, CancellationToken ct)
    {
        var out_ = new List<(string, string)>();
        for (int c = 0; c < hosts.Count && !ct.IsCancellationRequested; c += 32)
        {
            var chunk = hosts.Skip(c).Take(32).ToList();
            var names = await Task.WhenAll(chunk.Select(async ip =>
                (ip, name: await Scanner.ReverseDnsAsync(ip, ct))));
            foreach (var (ip, name) in names)
                if (name is not null) out_.Add((ip, name));
        }
        return out_;
    }

    private static async Task<List<(string, string)>> NbnsAllAsync(List<string> hosts, CancellationToken ct)
    {
        var out_ = new List<(string, string)>();
        for (int c = 0; c < hosts.Count && !ct.IsCancellationRequested; c += 32)
        {
            var chunk = hosts.Skip(c).Take(32).ToList();
            var names = await Task.WhenAll(chunk.Select(async ip =>
                (ip, name: await Scanner.NbnsNameAsync(ip, ct))));
            foreach (var (ip, name) in names)
                if (name is not null) out_.Add((ip, name));
        }
        return out_;
    }

    private static async Task<List<(string, string)>> HttpAllAsync(List<(string ip, int port)> httpPairs, CancellationToken ct)
    {
        var out_ = new List<(string, string)>();
        for (int c = 0; c < httpPairs.Count && !ct.IsCancellationRequested; c += 16)
        {
            var chunk = httpPairs.Skip(c).Take(16).ToList();
            var srvs = await Task.WhenAll(chunk.Select(async t =>
                (t.ip, srv: await Scanner.HttpServerAsync(t.ip, t.port, ct))));
            foreach (var (ip, srv) in srvs)
                if (srv is not null) out_.Add((ip, srv));
        }
        return out_;
    }
}
