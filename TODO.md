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

- (nothing — every intent through 1.2.1 shipped on both platforms; new
  requests land here after the generic/windows/macos scope check)

## Done

(archived at each release — history in tags and release notes)
