# AGENTS.md — FluentScanner

Native macOS build of FluentScanner (SwiftUI + Network.framework, Swift 6,
macOS 14+, Apple Silicon). No Xcode project: SPM + manual `.app` bundle.
Sibling: `windows/` (WinUI 3). Shared intents and release rules in root `AGENTS.md`.

## Build / run (SSD, never NAS)

The NAS has slow mechanical disks: NEVER `swift build` here. Sources are
copied to a **persistent** SSD dir and built there — the `.build/` cache
survives between runs, so recompiles are incremental (seconds, not minutes).
Only `dist/` artifacts come back; the build dir is never deleted.

```bash
S=/var/tmp/fscan-build && rm -rf $S/Sources $S/Package.swift $S/packaging \
&& cp -R Package.swift Sources packaging $S/ && cd $S
# gate: stop on build errors AND on self-test FAILs (grep matches FAIL
# too, so count it) — never ship a stale binary.
out=$(swift build -c release 2>&1); echo "$out" | tail -2
echo "$out" | grep -q "error:" && exit 1
fails=$(FS_SELFTEST=1 .build/release/FluentScanner 2>&1 | tee /dev/stderr | grep -c FAIL)
test "$fails" = 0
./packaging/bundle.sh   # dist/FluentScanner.app (binary, Info.plist, .lproj)
./packaging/dmg.sh      # dist/FluentScanner.dmg with /Applications symlink
```

Copy-back after a release build (from `macos/`):

```bash
D=dist && pkill -f FluentScanner.app/Contents/MacOS/FluentScanner; sleep 1
rm -rf $D/FluentScanner.app $D/FluentScanner.dmg
cp -R /var/tmp/fscan-build/dist/FluentScanner.app $D/
cp /var/tmp/fscan-build/dist/FluentScanner.dmg $D/
```

Headless self-tests: `FS_SELFTEST=1 <binary>` runs parser/ARP-table asserts and
terminates (`SELFTEST OK` / `SELFTEST FAILED` on stderr). The temporary scan
would be slow on a live network, so the self-test covers parsing + ARP table
only (offline-safe). Never ship test hooks — release binary keeps them (they
idle without the env var); verify a release run prints `SELFTEST OK`.

## Versioning / releases

Shared lockstep versioning, see root `AGENTS.md`: same X.Y.Z on both platforms
(same intent set); tags `macos-vX.Y.Z`; root `README.md` Changelog mirrors it.
Release + publish: `gh release create macos-vX.Y.Z dist/FluentScanner.dmg`,
commit + push `main`. `CFBundleShortVersionString` lives in `packaging/Info.plist`.

Every recompile bumps `CFBundleVersion` (build number, digits and dots,
max 18 chars) in the same `packaging/Info.plist`. Bump it on the **NAS source
before** copying to SSD: only `dist/` comes back from the temp build, an
increment made there would be lost. `CFBundleShortVersionString` changes only
at releases.

## Gotchas learned (skeleton — extend with real ones)

- ICMP raw sockets need root, but the `ping` binary doesn't: the sweep runs
  one `/sbin/ping -c 1` process per host, chunked, unprivileged, and every
  answer populates the local ARP table (that's the pass that gets MACs).
- NWConnection has no per-connection deadline: enforce timeouts with an
  `asyncAfter` **scheduled on the connection's own DispatchQueue**, so the
  cancel and the state callback never race.
- `.waiting` state → fail immediately (no route / ARP pending): a dead host
  answers nothing anyway and per-host timeout halves the scan time.
- ARP refresh happens *after* the discovery pass (TCP connects populate the
  table); merge with the pre-scan snapshot — stale entries keep `incomplete`
  (no MAC) from shadowing older good ones.
- Port scanning fires many sockets at once: chunk the (host, port) pairs —
  never all-in-one `withTaskGroup` on a /24.
- Entitlement-free: no packet sniffing, no promiscuous mode — only
  `arp -a`, DNS and TCP connects, so the app needs no special permissions.
- Results use a native SwiftUI `Table` (resizable columns for free):
  header clicks sort via `value:` key paths — pre-sort the stored rows
  with the same comparator so the CSV export matches the display order.
- Swift `String.split(separator: "\n")` never fires on CRLF text:
  `"\r\n"` is a single `Character` (grapheme cluster). Parse network text
  with `components(separatedBy:)` (NSString semantics) instead — this bug
  silenced every HTTP `Server:` header until the self-test caught it.
- Screenshots: find the app window via its `CGWindowNumber`
  (`CoreGraphics` one-liner) and capture with `screencapture -l <id>`;
  trigger a scan first with AppleScript
  (`tell application "System Events" to keystroke return`).
- Vendor (OUI): `oui.csv` in `Sources/FluentScanner/Resources/` ("aabbcc<TAB>Org",
  full unfiltered IEEE registry, ~40k rows, ~1.2 MB) is copied by bundle.sh
  next to the .lproj dirs; lookups via `Oui.vendor(mac)`. Regenerate from
  the `(hex)` lines of https://standards-oui.ieee.org/oui/oui.txt
  (key = lowercase hex without dashes) and update **both**
  `macos/.../Resources/oui.csv` and `windows/source/oui.csv`.
- Hostnames need four sources: the router publishes no PTR, so reverse DNS
  alone stays empty — mDNS harvest first (queries MUST end in `.local`,
  bare service types get zero answers; the A records live in the AR
  additional section, not AN; burst sent twice so flaky IoT responders are
  caught), UPnP M-SEARCH harvest (unicast replies need no multicast
  membership; description maker/friendlyName as second brand fallback),
  NetBIOS NBSTAT (UDP 137) for Windows PCs, reverse DNS last.
  Harvest-only ghosts get the full treatment too: post-harvest ARP re-read,
  rDNS + NetBIOS over all rows, ghost 80/8080 knock, re-classify of
  still-blank kinds.
