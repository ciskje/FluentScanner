# TODO.md — change tracker

Every requested change is classified **first** by scope, then appended here
before implementing:

- `generic` — shared intent: implement on **every** platform (each on its own
  machine/build). The row is done only when all platforms have it.
- `windows` / `macos` — platform-only: implement (and rebuild the
  app) only where relevant; other platforms ignore the row.
- Implement only for the current platform, update the row; Done rows are
  archived (deleted here) at each release — history lives in tags and
  release notes, not in this file.
- Generic intents are also mirrored in the parity checklist in `AGENTS.md`.

Legend per row: `win:[ ]` / `mac:[ ]` = shipped on that platform.

## Pending

- `generic` — harvest-ghost evidence: hosts seen only through mDNS/UPnP
  (no ping, no discovery port — e.g. assistants answering as `none`) get
  every clue: MAC from a post-harvest ARP re-read, reverse DNS + NetBIOS
  over the full row set, a short 80/8080 knock, the UPnP description
  (maker/friendlyName) as second brand fallback, mDNS query burst sent
  twice, `_privet._tcp.local` probed. `win:[x]` (1.2.6: `SsdpDiscoverAsync`
  + `SsdpDeviceAsync`/`ParseSsdpDescription`/`SsdpLabel` in `Scanner.cs`
  with self-tests, `SsdpAllAsync` + `GhostWebAsync` + full-row `snap` +
  post-harvest ARP/classify pass in `MainWindow.xaml.cs`; verified live:
  `.106 none-2` gained MAC + Amazon vendor).
  `mac:[ ]` — mirror: M-SEARCH to 239.255.255.250:1900 (unicast replies need
  no multicast membership), description GET with 2 s caps, same label rule
  (only when OUI/vendor silent), rDNS + NetBIOS over all `byIp` keys, ghost
  80/8080 probe, ARP re-read applied to all rows, re-run classify for
  still-blank kinds, add `_privet._tcp.local` to `mdnsTypes`.
- `generic` — live results + never stall on an unreachable resolver: hosts
  appear the moment they answer, the internet-dependent lookups run **after**
  the partial results are already on screen. `win:[x]` (chunk-by-chunk
  publishing in `SweepPingAsync`/`SweepTcpAsync`, per-chunk merges in
  `ReverseAllAsync`/`NbnsAllAsync`/`HttpAllAsync`, `HostRow.NameRank`
  3/2/1 precedence; reverse DNS already capped at 1500 ms).
  `mac:[x]` — shipped (reverseDNS 1.5 s deadline on a concurrent queue,
  per-chunk `publishAll()` in `sweepPing`/`sweepTCP`, per-chunk + per-source
  merges with `HostRow.hostnameRank` 3/2/1 precedence; `CFBundleVersion` 33).
  Done on both platforms — archive at the next release.
- `windows` — ARP icon in Settings > Installed apps: add
  `UninstallDisplayIcon={app}\FluentScanner.exe` to
  `windows/installer/FluentScanner.iss` (per skill `windows-setup`
  template). `win:[x]` (`.iss` + rebuilt `installer/dist/...-1.2.2.exe`;
  takes effect on reinstall).
- `generic` — size-to-fit button: a toolbar button fits every column to
  its contents (macOS: ↔ icon + Cmd+Shift+F, the fitted set pinched so it
  never overflows the window). `mac:[x]` (1.2.3). `win:[ ]` — add the same
  button to the WinUI toolbar (its Excel-style boundary drag/double-click
  stays as a bonus).

## Done

(archived at each release — history in tags and release notes)
