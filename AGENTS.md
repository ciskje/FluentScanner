# AGENTS.md — FluentScanner (Windows + macOS)

One repo, two native builds of a LAN scanner (IP range → live hosts,
service indications, MAC addresses):

- `windows/` — WinUI 3 / .NET 8, unpackaged (no MSIX).
- `macos/` — SwiftUI + Network.framework, Swift 6, macOS 14+, Apple Silicon.

Platform details (build, gotchas, paths) live in `windows/AGENTS.md` and
`macos/AGENTS.md`. This file holds only what is shared: intents, workflow,
versioning and releases. Build on each machine **only for its own platform**.

## Feature intents (parity checklist)

Both builds implement the same intents; platform-native details may differ:

- Target parsing: single IP, CIDR (`192.168.1.0/24`, network + broadcast
  never probed), or inclusive IP range (`192.168.1.1-192.168.1.254` full
  IPs, or `192.168.1.1-254` last octet only). The toolbar edits start/end
  IPs; Get IP fills the local subnet range.
- Host discovery: ICMP ping sweep (unprivileged: parallel `ping`
  processes on macOS, `System.Net.NetworkInformation.Ping` on Windows — it
  also populates the ARP table for every responder) plus a TCP
  discovery-port pass; a host found by either is alive (implementations
  may overlap the two passes)
- Open-port scan of a fixed common-services list for every live host
- MAC addresses from the local ARP table (`arp -a` on both platforms; the
  TCP connects and ICMP pings populate the entries on the local subnet);
  hosts still missing an entry get a targeted re-ping + ARP re-read.
  Locally-administered (randomized) MACs are flagged and never OUI-blamed.
- Device vendor/type hints: MAC OUI prefix (bundled IEEE-derived table)
  combined with service hints ("Windows host" for RDP/WinRM, "Printer",
  "NAS", "Router/AP", "IoT device"…), plus a ping-reply TTL guess as
  fallback hint (128→Windows host only — 255 is the default of embedded
  stacks like Tuya/Espressif, not a router signature), the HTTP
  `Server:` header as brand fallback when OUI is silent,
  shown in the same column when OUI/ports explain nothing, and the UPnP
  device description (M-SEARCH harvest + description maker/friendlyName)
  as a second brand fallback in the same slot
- Hostnames, best effort (no failure is fatal): mDNS harvest first
  (Bonjour `.local` names — queries need the `.local` suffix, A records
  ride in the additional section; the query burst goes out twice so flaky
  IoT responders are caught), NetBIOS node-status for Windows PCs,
  classic reverse DNS last. Hosts seen only through harvests (no ping,
  no discovery port) still get every clue: MAC from a post-harvest ARP
  re-read, reverse DNS + NetBIOS over the full row set, a short 80/8080
  knock, and the UPnP label
- Live results list: IP (with a device-type color dot), hostname, MAC,
  vendor (each brand in its own stable color) and type in separate columns,
  discovered services
  (HTTP/HTTPS entries open the responding host in the browser), latency
  (ping round-trip, else TCP connect time of the first open port); header
  clicks sort by column and a text filter narrows the list; a toolbar
  button sizes every column to its contents (macOS: ↔ icon, Cmd+Shift+F;
  the fitted set must never push a column out of view — macOS pinches the
  fitted widths to the visible table); column dividers can be dragged to
  resize (Windows also shows the west-east cursor on the boundary and
  sizes on a boundary double-click as a bonus); a color legend
  sits in the footer; the scanning host itself is always listed and
  highlighted (accent IP + computer icon) — the highlight must
  stay readable in both light and dark mode (pure yellow fails on light
  backgrounds, use a darker amber there)
- Results land live: a host appears the moment it answers (the discovery
  passes publish chunk by chunk, hostname lookups merge as each chunk
  answers), and every lookup that depends on the network having a working
  resolver has a hard per-host deadline — a scan must never sit waiting on
  something that can never arrive (an AP with no internet) once partial
  results are already on screen
- Progress bar, stop mid-scan, CSV export; the header underline follows the
  theme (never the progress accent) and a spinner next to the status marks
  a running scan, so final results are recognizable
- Light/dark/system theme (selectable in Settings), UI in EFIGS
  (override in Settings: system/EN/FR/IT/DE/ES)
- Settings: table font size + monospaced toggle; the discovery/scan port
  lists and the ping/discovery/port timeouts have fixed working defaults,
  editable in the app
- Help (Apple Help Book with chapters + menu search) + About panel, in all
  supported languages; a separate Novità/What's new sheet (own Help-menu item, every
  version dated) carries the changelog in all supported languages
- App icon: radar-sweep SVG source, rendered per platform

## Change workflow

1. A change request updates the intents above first (this file).
2. Implement **only for the local platform**; the other platform implements
   the same intent later on its own machine.
3. Same rule for fixes: if the bug is in a shared intent, mirror the fix.

## TODO.md (classify first)

On every change request, first decide the scope — `generic`, `windows`
or `macos` — and append a row to `TODO.md` with the platform note
before implementing:

- `generic`: implement locally now; the row stays open until every platform
  has it (next build on another platform picks it up from TODO.md).
- platform-only: implement and rebuild the app locally; others ignore it.
- Mark the local platform done in the row; mirror generic intents here too.
- Done rows are archived (deleted here) at each release; history lives in
  tags and release notes, not in TODO.md.

## Versioning (lockstep X.Y.Z)

Same version number on both platforms so changelogs stay comparable:
`X.Y.Z` = the same intent set. A platform publishes `X.Y.Z` when it has
implemented those intents; if the other lags, its release waits.

- Z = bugfix/tweak, Y = new feature/behavior, X = upheaval.
- Docs/refactor/build never change the version.
- Current: Windows `1.2.6`, macOS `1.5.0` (1.2.2 was Windows-only:
  Excel-style column resize; macOS got the header double-click in 1.2.3;
  1.2.4 fixes the size-to-fit button, which 1.2.3 had broken; macOS skipped
  the 1.2.5 release and caught up at 1.2.6; 1.2.7–1.5.0 ship macOS tweaks
  whose Windows mirrors are still open — same intents once they land).
- Single source: `<Version>` in `windows/source/FluentScanner.csproj`
  and `CFBundleShortVersionString` in `macos/packaging/Info.plist`.

## Tags & releases

One channel per platform, tags always carry the platform prefix:

- Windows: tag `windows-vX.Y.Z` + `FluentScanner-Setup-X.Y.Z-win-x64.exe`
  (from `windows/installer/FluentScanner.iss`).
- macOS: tag `macos-vX.Y.Z` + `FluentScanner.dmg` (drag into Applications).
  Command in `macos/AGENTS.md`.
- Every version bump is published (tag + release with artifact), otherwise
  Latest lags behind the code. Keep the **last 2 releases per platform**.
  Every bump also appends its changelog entry to the README Changelog and
  to `changelogBody` in every `.lproj` (macOS) / resw (Windows) — the
  in-app Novità/What's new must never lag behind the releases.
- Release notes **always in English**: standard install line + changelog
  entries since the previous release of that platform (same text as the
  Changelog in `README.md`, which is in English).

## "Generate the application" (build for the current environment)

"Generate the application" always means: build **only for the platform of the
machine the agent runs on**, never the other one. Commands in
`windows/AGENTS.md` (Build/run) and `macos/AGENTS.md` (Build/run).
Don't touch the version (except releases), don't publish
to GitHub unless explicitly requested.

## Build locations (both platforms)

Sources live on the NAS (slow mechanical disks): never build there.
Each platform builds in a local-SSD temp dir and copies only the final
artifacts back — Windows via MSBuild `BaseOutputPath` /
`BaseIntermediateOutputPath` into `windows/dist/`, macOS via the persistent
`/var/tmp/fscan-build` into `macos/dist/`. Both `dist/` dirs are gitignored,
never versioned.

## Source language

Source everything in English: comments, docs, sample file names.
Exception: UI strings (WinUI `.resw` it-IT/en-US, macOS `.lproj`), localized;
code-side strings go through the platform loader (`Str()` / `L()`).

## Line endings

`* text=auto eol=lf` everywhere (LF working copies on all platforms —
the Mac builds from this repo too); only `*.sh` → LF and `*.bat` → CRLF
are enforced explicitly. This also silences the
"LF will be replaced by CRLF" warnings that `core.autocrlf=true`
would otherwise emit for every file.
