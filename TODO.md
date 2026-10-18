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

- `generic` — 1.1.0 editable ports + timeouts, sorting, filter, readable
  self highlight. `mac:[x] win:[ ]`
- `generic` — settings for the port list and timeouts (1.1.0).
  `win:[ ]` `mac:[x]`
- `generic` — theme selector light/dark/system via Settings (0.10.0).
  `win:[ ]` `mac:[x]`
- `generic` — result sorting (click a column header) and text filter
  (1.1.0). `win:[ ]` `mac:[x]`
- `macos` — copy-back also brings `dist/FluentScanner.dmg` back from the
  build dir (it stayed in `/var/tmp/fscan-build`, only the app came back);
  step fixed in `macos/AGENTS.md`. `mac:[x]`
- `macos` — 1.1.0 publish: get `FluentScanner.dmg` onto the NAS, then tag
  `macos-v1.1.0` + `gh release create` with it. `mac:[x]`

## Done

(archived at each release — history in tags and release notes)
