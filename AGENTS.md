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
  stacks like Tuya/Espressif, not a router signature), and the HTTP
  `Server:` header as brand fallback when OUI is silent,
  shown in the same column when OUI/ports explain nothing
- Hostnames, best effort (no failure is fatal): mDNS harvest first
  (Bonjour `.local` names — queries need the `.local` suffix, A records
  ride in the additional section), NetBIOS node-status for Windows PCs,
  classic reverse DNS last
- Live results list: IP (with a device-type color dot), hostname, MAC,
  vendor (each brand in its own stable color) and type in separate columns,
  discovered services
  (HTTP/HTTPS entries open the responding host in the browser), latency
  (ping round-trip, else TCP connect time of the first open port); header
  clicks sort by column and a text filter narrows the list; double-click
  a header sizes it to its contents; a color legend
  sits in the footer; the scanning host itself is always listed and
  highlighted (accent IP + computer icon) — the highlight must
  stay readable in both light and dark mode (pure yellow fails on light
  backgrounds, use a darker amber there)
- Progress bar, stop mid-scan, CSV export
- Light/dark/system theme (selectable in Settings), UI in English and
  Italian (override in Settings: system/EN/IT)
- Settings: table font size + monospaced toggle; the discovery/scan port
  lists and the ping/discovery/port timeouts have fixed working defaults,
  editable in the app
- Help sheet + About panel, in both languages
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
- Current: Windows `1.2.0`, macOS `1.2.0` (the macOS artifact for 1.2.0 is
  built on the Mac; intents are already in the source).
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
