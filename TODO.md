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

- `generic` — EFIGS UI (EN/FR/IT/DE/ES): every UI string, the Help Book and
  the changelog in five languages (system language outside EFIGS → English).
  `mac:[x]` — fr/de/es `.lproj` + help-src (8 chapters each, hiutil-indexed),
  `L()` + picker cover EN/FR/IT/DE/ES, `CFBundleLocalizations` set
  (shipped in 1.4.0).
  `win:[ ]` — resw `Strings/fr-FR,de-DE,es-ES` + `UiLang` non-it→`en` +
  language menu + Help/changelog translations (Windows rebuild + verify
  there).
- `generic` — changelog in Help (EFIGS): a separate Novità/What's new
  entry with its own Help-menu item, every version dated. `mac:[x]`
  (1.3.0: `ChangelogView` + dated `changelogBody` in both `.lproj`).
  `win:[x]` — resw `changelogItem/Title/Body` + Help-menu item opening a
  changelog dialog (code written from the macOS machine — Windows rebuild
  + FSCAN_SELFTEST pending there).
- `generic` — structured multi-chapter help with working search (EFIGS):
  macOS ships an Apple Help Book (`packaging/help-src`, hiutil-indexed in
  `bundle.sh`, opened from the Help menu + toolbar ?); Windows mirrors with
  chapters + in-dialog search. The dated Novità/What's new sheet + its own
  Help-menu item stay as they are.
  `mac:[x]` — Apple Help Book (`help-src`, hiutil index, `openHelpBook`;
  `HelpView` sheet deleted; shipped in 1.5.0). `win:[x]` — Help dialog with 5 chapters + live
  search box, `helpBody` replaced by chapter keys (code written from the
  macOS machine — Windows rebuild + FSCAN_SELFTEST pending there).
- `generic` — third-language default: a system language outside EN/IT shows
  English (never raw keys).
  `win:[x]` (`UiLang` non-it→`en`; code written from the macOS machine —
  Windows rebuild + FSCAN_SELFTEST pending there). `mac:[x]` (`L()` falls back to `en`;
  `;` port separator also shipped on mac: `parsePorts` splits `;` like
  `,`/space, `win:[x]` already so — mac side shipped in 1.2.7).
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
