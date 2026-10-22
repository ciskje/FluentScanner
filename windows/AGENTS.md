# AGENTS.md — FluentScanner

WinUI 3 / .NET 8, unpackaged (no MSIX). LAN scanner (ping + TCP discovery,
common-ports sweep, ARP MAC lookup, mDNS/NetBIOS hostnames). Sibling: `macos/`
(SwiftUI). Shared intents and release rules in root `AGENTS.md`.

## Build / run (SSD temp, never NAS)

The NAS has slow mechanical disks: NEVER build in `source/bin`+`source/obj`.
Sources stay on the NAS; ALL generated/cached files go to a local-SSD temp
dir via MSBuild properties, then only the final artifact comes back to the
NAS (`windows/dist/`, gitignored like `macos/dist/`).

```bash
# NB: -p:, not /p: — msys bash mangles /p: as a path (MSB1008).
TMP=C:/Temp/opencode/fscan-build
dotnet build source/FluentScanner.csproj -c Debug \
  -p:BaseIntermediateOutputPath="$TMP/obj/" -p:BaseOutputPath="$TMP/bin/"
# An AfterBuild target copies the output to windows/dist/FluentScanner/
# (SkipUnchangedFiles, so incrementals stay cheap) — the runnable app lives
# there, mirroring macos/dist/FluentScanner.app.
```

Or double-click `start-debug.bat` (builds to temp, launches from `dist/`).

Release + installer (requires Inno Setup 6):

```bash
dotnet publish source/FluentScanner.csproj -c Release -p:Platform=x64 \
  -p:BaseIntermediateOutputPath="$TMP/obj/" -p:BaseOutputPath="$TMP/bin/" \
  -p:PublishDir="$TMP/publish/"
# then run (CopyPublishToNas already synced it to windows/publish/):
iscc installer\FluentScanner.iss   # output in installer\dist\
```

- EVERY `dotnet` invocation (`build`, `publish`, even bare `restore`)
  must carry the redirect properties — otherwise `source/bin`+`source/obj`
  regenerate on the NAS (a bare `restore` already did once: only
  `project.assets.json` files appeared). The only NAS outputs are the final
  artifacts: `windows/dist/` (AfterBuild copy) and `windows/publish/`
  (CopyPublishToNas on Release builds, consumed by the `.iss`).
- NEVER let `source/bin` or `source/obj` exist on the NAS (delete if a
  stray command recreates them): with `Base*` redirected to temp the
  default bin/obj excludes stop applying, and anything under `source/`
  gets globbed as build inputs — stale copies cause duplicate-symbol
  errors or PRI conflicts (`PRI277 Conflicting values`, both verified).
- `dotnet publish` does NOT copy the app's own `FluentScanner.pri` /
  `App.xbf` / `MainWindow.xbf` to the publish dir (framework `.pri` files
  are fine): without them the installed app dies at startup with
  `0xC000027B` in `Microsoft.UI.Xaml.dll` (verified). The
  `CopyAppResourcesToPublish` target (`AfterTargets="Publish"` in the
  csproj) copies them over on every publish — never remove it.
- Incremental by default: never `dotnet clean`, `Rebuild`, or delete the
  temp build dir, or every build pays the full price.
- Publish output layout keeps the `x64` level from `-p:Platform=x64`; the
  `.iss` `PublishDir` points at the NAS copy of it.

## Versioning (X.Y.Z)

Windows-only detail (shared rules in root `AGENTS.md`): single source is
`<Version>` in `source/FluentScanner.csproj`; `#define MyAppVersion` in
`installer/FluentScanner.iss` and the setup name in the root `README.md`
derive from it (never bumped for docs/refactor/build).

## GitHub releases

Command (the rest — English notes, last 2 per platform — in root `AGENTS.md`):

```bash
git tag windows-vX.Y.Z && git push origin windows-vX.Y.Z
gh release create windows-vX.Y.Z "installer/dist/FluentScanner-Setup-X.Y.Z-win-x64.exe" \
  --title "windows-vX.Y.Z" --notes "<changelog summary>"
```

## Windows 11 UI guidelines (Mica + title bar + menu)

Standard Win11 window, no custom chrome beyond this:

- `SystemBackdrop = new MicaBackdrop()` in the ctor. Mica is REQUIRED for
  the theme to render at all (see theme gotcha below); on Win10 it is
  silently ignored and the theme-colored background shows instead.
- Custom title bar: `ExtendsContentIntoTitleBar = true` + `SetTitleBar()`
  on a 32px drag-region grid (app icon via `BitmapImage` from
  `Assets/AppIcon.ico` next to the exe + title text). Taskbar icon via
  `AppWindow.SetIcon()` with the same file.
- Standard `MenuBar` (File / Scan / View / Help) instead of toolbar
  buttons for app commands; the scan toolbar keeps only range inputs
  (start/end IP + the results filter box).
  View menu owns all former settings-dialog options (theme + language
  radio groups, monospaced toggle, font-size 10–16 radios) and opens the
  `Settings…` dialog (discovery/service port lists + ping/discovery/port
  timeouts — `AppSettings.ApplyTuning()` pushes them into `Scanner`).
- Self-host highlight is theme-aware: yellow IP on dark, dark amber
  `#8A6D00` on light (pure yellow is unreadable on white).
  `HostRow.DarkMode` is set from `EffectiveDark()` (settings override,
  else the `AppsUseLightTheme` registry value, same rule as FluentNotepad);
  rows refresh via `RefreshTheme()` on every `ApplySettings()`.

## Unpackaged constraints (no package identity, same as FluentNotepad)

- **No MRT/`ResourceLoader` from code**: the constructor throws
  `FileNotFoundException`. Code-behind strings (`Str()` in
  `MainWindow.xaml.cs`) read the `.resw` files as XML from `Strings/<language>/`
  next to the exe. `x:Uid` in XAML works instead (internal MRT).
- **No `ApplicationData.LocalSettings`**: settings are a JSON file at
  `%LocalAppData%\FluentScanner\settings.json` (`Settings.cs`).
- **App.xaml must merge `XamlControlsResources`**: without it no default
  style exists (`CaptionTextBlockStyle`, theme brushes…) and the first
  `InitializeComponent` dies with `XamlParseException`
  (`Cannot find a Resource with the Name/Key …`) → instant `0xC000027B`.

## Scan engine notes (mirror the macOS identical-intent details)

- ICMP unprivileged via `System.Net.NetworkInformation.Ping` (ping sweep in
  chunks of 64) runs **concurrently** with the TCP discovery pass over all
  targets (union of hits, wall time ≈ the slower pass); both populate the
  ARP table. Progress tracks `(pingTested + tcpTested) / 2*total`.
- Discovery probes 445, 80, 8080, 22, 443, 3389, 9100, 631; the port sweep
  runs the common-services list (`DefaultScanPorts` in `Scanner.cs`). Both
  are built-in defaults only: the Settings dialog overrides them
  (`AppSettings` port texts → `ApplyTuning()`), exactly like the macOS
  UserDefaults-backed `discoveryPorts` / `scanPorts`. Keep the defaults
  identical to `macos/Sources/FluentScanner/Scanner.swift` and the port
  names in sync, so results stay comparable across platforms.
  `ParsePorts` (1-65535, deduped, max 256, invalid → null) mirrors the
  macOS `parsePorts` and is exercised by the self-test.
- MAC via `arp -a` text parse (`Scanner.ArpTable()`), refreshed **after** the
  discovery pass (the TCP connects populate it); both platforms share the
  same two-format line parser. Responders with still-missing entries get a
  targeted re-ping + one more ARP re-read.
- Hostnames, best effort, concurrent: mDNS harvest (`.local` queries, A
  records in the additional section) → reverse DNS → NetBIOS node-status;
  the HTTP `Server:` header fills the brand slot only when OUI is silent
  (only for already-open 80/8080, annotated `HTTP/Caddy (80)`).
- TTL hint: only 128 → "Windows host" (`TtlKind`); 255 is embedded stacks.
- Local interfaces via `NetworkInterface` (no `getifaddrs` on Windows):
  `LocalSubnet()` powers Get IP, `LocalAddrs()` pre-inserts + highlights
  the scanning host row.
- Results arrive per host as the scan progresses (UI updates live): the ping
  and TCP discovery sweeps publish each chunk as soon as it answers, and the
  phase-3 hostname lookups merge per chunk. `HostRow.NameRank` keeps the
  mDNS(3) > reverse DNS(2) > NetBIOS(1) precedence whatever the arrival order;
  `ReverseDnsAsync` caps every lookup at 1500 ms, so a network with no working
  resolver (an AP with no internet) can never hold the scan open. macOS still
  needs the same (see TODO.md).

## WinUI gotchas learned

- XAML namespaces are `winfx`, not `winui`: `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"`.
  The `winui` spelling compiles zero types — every control reports WMC0001.
- If the C# build fails, the XAML compiler reports bogus cascading errors:
  look at the CS errors first (same as FluentNotepad).
- **PRI175 collision**: a code-behind `.resw` key must not collide
  case-insensitively with any `x:Uid` scope (`ExportCsv/Content` scope vs
  `exportCsv` key). Prefix code keys (`btnStart`, `thIp`, …).
- No `WrapPanel` in WinUI — legend uses a horizontal `StackPanel`.
- `FontWeight` struct is `Windows.UI.Text`, the `Bold`/`Normal` helpers are
  `Microsoft.UI.Text.FontWeights` (mixed namespaces).
- `Window.Content.RequestedTheme` doesn't exist — cast first:
  `((FrameworkElement)Content).RequestedTheme`.
- **RequestedTheme alone does NOT darken the window**: with no
  `SystemBackdrop`, the client area stays light even with
  `RequestedTheme=Dark` hardcoded in XAML and `ActualTheme` reporting
  `Dark` (verified by trace). Mica paints the themed surface — set it.
- **Every custom color needs a light AND a dark variant** (`HostRow`
  `KindColorsLight/Dark`, `BrandPaletteLight/Dark`, amber-vs-yellow self
  IP): fixed hues go unreadable on one theme (yellow IP on white,
  dark-red brand on black). Switch on `DarkMode`, notify all three
  brushes in `RefreshTheme()`.
- Never snapshot `Application.Current.Resources[...]` in code: it resolves
  with the *system* theme, so with OS-light + app-dark the legend labels
  came out black-on-dark. Resolve from the active theme dictionary
  (`ThemeDictionaries["Dark"/"Light"]`) with a hardcoded fallback, and
  rebuild on every `ApplySettings()`.
- Results UI uses a `ListView` + header `Grid` — WinUI has no `DataGrid`;
  keep header and item `ColumnDefinition` widths in sync manually (shared
  `ColumnWidths` resource in `App.xaml`, gripper drag in the header).
- The scan engine runs on a threadpool thread (`Task.Run` in `Start_Click`);
  every row/progress/status touch goes through `DispatchAsync`/`Post`
  (`DispatcherQueue`). Never touch `_rows`/`_byIp`/controls from pool
  threads directly — `ObservableCollection` and bindings are UI-affine.
  Verified: resize/move stay ~100 ms mid-scan.
  Tabular look: per-cell right `Border`s with a theme-aware `RowSeparator`
  brush, zebra `RowBackground` driven by `RowIndex` set in `SortRows()`,
  accent header underline + semibold headers.
- Sorting/filtering lives entirely in `SortRows()`: `_byIp.Values` →
  filter (`MatchesFilter`: ip/hostname/mac/vendor text/services, same
  fields as the macOS `publishAll`) → `CompareRows` (`_sortCol`/`_sortAsc`,
  IP tie-break) → refill `_rows`, renumbering `RowIndex` for the zebra.
  So the visible order *is* the CSV export order (mirrors macOS). Header
  clicks are `Tapped` on the header `TextBlock`s (`Tag` = column index,
  arrow appended by `RefreshHeaders()`), not buttons: no restyling of the
  caption headers, and the gripper `Rectangle` stays on top of the strip.
  A row created while a filter is active is only added if it matches; the
  next `SortRows()` reconsiders every row once data fills in.
- Header gestures: single click sorts, **double-click sizes the column to
  its contents** (`AutoFitColumn`: every visible row measured with a
  throwaway `TextBlock` — the rows are virtualized, so realized containers
  only cover the viewport; header title + the widest cell + per-column
  chrome, clamped 60–640 px). WinUI delivers the first tap of a
  double-click as a plain `Tapped`, so `Header_DoubleTapped` (which does
  fire, but **only with the `DoubleTapped` attribute wired in XAML**)
  restores the pre-tap sort order first: a double-click must not move the
  order. The same gesture works Excel-style on the column boundary
  (`Gripper_DoubleTapped` on the gripper element, `Tag` = the column index);
  a single click on the boundary neither sorts nor resizes.
  A long cell can push a column past the viewport (the ListView scrolls
  horizontally — the header, being sticky, is what UIA reports as clipped).
- Column gripper geometry: the boundary handle is a 12px
  `CursorGripper` centred on the column edge (`Margin="0,0,-15,0"` against
  the caption, which sits 9px inside the column: 6px each side, the 12px
  `ColumnSpacing` gap keeps it clear of the next caption). It is invisible
  until hovered (opacity 0 → 0.7 → 1 while dragging).
- **The drag must read a stable reference frame**: the gripper rides the
  column's right edge, so `GetCurrentPoint(gripper)` moves with the very
  thing being resized and the deltas cancel out (the column grew ~3px
  instead of 60, verified). `Gripper_Press`/`Gripper_Move` measure against
  `HeaderGrid` instead.
- **Per-element cursor (`CursorGripper`)**: WinUI 3 has no public cursor
  property — `UIElement.ProtectedCursor` is protected, so it only works
  from a derived class, and the obvious bases are sealed
  (`Rectangle` and `Border` both throw CS0509), hence a `Grid` subclass.
  Set it in the ctor **and** on `Loaded` (before the element is in the
  visual tree it can be applied too early). The old
  `Window.Current.CoreWindow.PointerCursor` code compiled but did nothing:
  WinUI 3 desktop apps have no CoreWindow, so the cast was always null
  (and it silently swallowed by a `catch`).
  Caveat: the harness cannot observe the resulting cursor shape —
  `GetCursorInfo` returns the same handle over the app's TextBox (I-beam),
  a HyperlinkButton (hand) and the header (arrow), i.e. it is per-window
  here, so the shape needs an eyeball check.
- **Never bind null to a Brush in `x:Bind`**: on virtualized (recycled)
  `ListView` containers the text stays invisible even though UIA reads the
  right value — see the blank-IP bug (only the self row, with its explicit
  brush, rendered). Always resolve an explicit brush (`TextPrimaryBrush()`
  from the active theme dictionary).
- Vendor (OUI): `oui.csv` ships as Content next to the exe and is read by
  `Scanner.Vendor`; same file as `macos/.../Resources/oui.csv` (regeneration
  note in `macos/AGENTS.md`).

## Headless checks

- Self-test (mirrors macOS `FS_SELFTEST=1`): `FSCAN_SELFTEST=1 <exe>`
  runs offline-safe parser/classify asserts, prints `SELFTEST OK` on stderr,
  exits 0/1. Release binary keeps it (idles without the env var).
- Smoke start: **copy the exe dir to the local SSD first** (SmartScreen
  stalls unsigned exes on network drives; NAS I/O is slow), launch, wait,
  screenshot with `CopyFromScreen` via PowerShell, kill the process.
- In this repo's shell, `cmd.exe /c` drops its arguments — use PowerShell
  (`Start-Process -PassThru` + poll `HasExited`/`ExitCode`), and always `cd`
  with an absolute path (the tool `workdir` is not always applied).
  Launching a GUI app inherits the shell's pipes and hangs the tool:
  never `WaitForExit`, poll instead. Redirects with backslashes create
  literal `C:Tempo...` files in the cwd — use forward slashes.
- Exit `0xC000027B` at startup = managed `XamlParseException` crossing the
  WinRT boundary (missing style/resource, e.g. no `XamlControlsResources`).
  Add a temp `File.AppendAllText` trace in the ctor to pinpoint it.
