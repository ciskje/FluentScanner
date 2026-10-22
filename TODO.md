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

- `generic` — live results + never stall on an unreachable resolver: hosts
  appear the moment they answer, the internet-dependent lookups run **after**
  the partial results are already on screen. `win:[x]` (chunk-by-chunk
  publishing in `SweepPingAsync`/`SweepTcpAsync`, per-chunk merges in
  `ReverseAllAsync`/`NbnsAllAsync`/`HttpAllAsync`, `HostRow.NameRank`
  3/2/1 precedence; reverse DNS already capped at 1500 ms).
  `mac:[ ]` — reported slow on an AP with no internet ("waiting for something
  that will never arrive"). Three things to change in `macos/Sources/FluentScanner/`:
  1. `Scanner.reverseDNS` (`Scanner.swift`, "MARK: Reverse DNS") has **no
     deadline**: `getnameinfo` blocks for many seconds when the resolver is
     unreachable, and it runs on `DispatchQueue.global()` (limited
     concurrency, so stalled lookups queue up and multiply). Mirror Windows'
     1500 ms cap: `withCheckedContinuation` + `asyncAfter` deadline on a
     **concurrent** queue (`DispatchQueue(label: "FluentScanner.dns",
     attributes: .concurrent)`), first finisher wins, NSLock-guarded `finish`
     so the continuation is resumed once (same pattern as `probe`).
  2. `sweepPing` / `sweepTCP` (`ScannerVM.swift`) collect the hits and only
     `publishAll()` once **both** passes finish: insert the hit rows as each
     chunk lands (latency + TTL kind), `publishAll()` at the end of every
     chunk. MACs stay empty until the ARP re-read — that is fine, the row is
     already visible.
  3. Phase 3 awaits all four lookups and merges them in one block: merge each
     source the moment it lands. Add `hostnameRank` to `HostRow`
     (mDNS 3 > reverse DNS 2 > NetBIOS 1) and a `setHostname(ip, name, rank)`
     that only overwrites a lower rank, so precedence survives arrival order.
  Bump `CFBundleVersion` in `macos/packaging/Info.plist` on the rebuild; the
  version stays 1.2.x until both platforms ship the intent.
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
