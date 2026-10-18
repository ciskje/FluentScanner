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

- `generic` — Excel-style column resizing: the pointer on the column
  boundary turns into the resize cursor, dragging the boundary resizes the
  column, double-clicking the boundary sizes it to its contents.
  `win:[x]` (1.2.2: centred 12px boundary handle with the west-east cursor,
  drag via the header grid reference frame, `Gripper_DoubleTapped`).
  `mac:[ ]` — the native table divider already drags and shows the cursor;
  confirm that a double-click exactly on the divider also fits the *left*
  column (today the header-band double-click fits the column under the
  pointer).

## Done

(archived at each release — history in tags and release notes)
