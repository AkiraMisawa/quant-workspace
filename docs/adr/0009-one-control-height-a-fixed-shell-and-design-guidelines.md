# ADR-0009: One Control Height, a fixed shell, and design guidelines the client is held to

Status: PROPOSED 2026-11-06. Raised by the owner: the AI and theme buttons "do not align with Region and As of", and
"I want to improve the app's design, research it seriously and write guidelines for developing this repository"
(2026-11-06). Builds on ADR-0001 (Mantine, themed into a distinct visual system) and ADR-0002 (keyboard-first, UX02,
UX03).

## Context

ADR-0001 chose Mantine and asked for a visual system of the workbench's own, and `client/src/theme.ts` set its scales.
Nothing said how components use them, and each component chose its own size:

- The header's Commands, AI and theme controls were 22px (`Button size="compact-xs"`, `ActionIcon size="sm"`); the
  Region and As-of inputs beside them were 30px (`size="xs"`) with their labels stacked above. The row was centred on
  label-plus-input, so the buttons' middle sat 10px above the inputs' and their bottom edge 14px above the inputs'. The theme
  icon was a text glyph (`☀`, `☾`), drawn by whichever fallback font had it.
- The header's height was `auto` below `sm`. AppShell offsets the main area by the header's declared height, so on a
  phone the wrapped header covered the top of every panel: the Rates toolbar's first row was under it.
- At 768px, a single-row header would put the Region field under the title: the row needs about 850px.
- `focusRing: 'always'` left the amber ring on a button after a mouse click, which reads as a selected state.

None of these shows in a unit test, and each looked acceptable in the component that made it.

## Decision

1. **`docs/design-guidelines.md` is how the client is designed.** A UI change follows it or changes it first, in the
   same pull request. It is written from the sources it cites (Carbon, Fluent 2, Spectrum, WCAG 2.2, Material and
   Android window classes, Mantine's own documentation, Bloomberg's design team, Strom on density) and from what this
   client got wrong. MUST rules are held in review; SHOULD rules are defaults that a pull request may depart from with
   its reason.
2. **One Control Height per surface.** A row of controls is 30px (Mantine `xs`; `ActionIcon size="input-xs"`); a
   dialog's form is 36px (`sm`). Controls of different heights never share a row.
3. **A toolbar's labels are beside its inputs.** `ContractFields` takes `layout="toolbar"`: the label to the left, the
   input one Control Height, an invalid field marked on its edge, and the message said in the main area
   (`ContextIssues`), so a message never changes the toolbar's height.
4. **The shell's geometry is fixed per breakpoint.** `HEADER_HEIGHT` is 44px from `md` (992px) and 82px (two rows)
   below it, never `auto`. The fold is at `md` because that is where the header's content fits, as measured.
5. **Icons come from `@tabler/icons-react`** (3.50.0, exact), at 16px in a 30px control. No text glyph is an icon.
6. **The focus ring follows `:focus-visible`** (`focusRing: 'auto'`): always for the keyboard, never as the
   remainder of a click or tap.
7. **Grids are drawn by `@ex-grid/mantine`, and the workbench takes the grid's font.** Asked by the owner on
   2026-11-06 ("switch to @ex-grid/mantine"). `WorkbenchGrid` wraps `MantineExGrid` with the paper decided once
   (bordered, square), and the hand-written `--ex-` bridge in `theme.css` is removed (ADR-0001's DEFAULT, rewritten).
   The theme's `fontFamily` is `mantineDefaultFont.family`, Mantine's default system stack, the face whose widths
   ExGrid judges `####` by: a figure reads in one face in a grid and beside it, and no web font is loaded. Inter was
   named before and never loaded, so each platform drew a different fallback.
8. **Geometry that only a browser shows is tested in one.** A layer-3 test reads the header's controls at 1440, 992,
   768 and 390px and holds one Control Height, shared rows, nothing past the right edge, and a main area that starts
   below the header.

DEFAULT: the 30px Control Height stays on touch screens. OPEN: whether a coarse pointer raises the shell's controls
toward 44px, decided on the iPhone journey (UX02) on a real device.

## Consequences

- The header is three zones on one row from `md` up: title and Commands; Region and As of; AI and theme, behind a
  divider. Below `md` the context takes a second row; below `sm` its two fields share that row's width.
- The AI toggle shows its open state with the `light` variant and `aria-pressed`, instead of a filled amber button
  with a focus ring.
- New dependencies, `@tabler/icons-react` and `@ex-grid/mantine`, pinned exactly like the rest of
  `client/package.json`; `@ex-grid/mantine` moves with the ExGrid family's version.
- The grids' headers are the body's colour at weight 600, Mantine's Table look, instead of the grey band the
  hand-written bridge drew; their menus and filter panel are Mantine's buttons, selects and radios.
- The guidelines list what was seen and left (§12): truncated Rates column headers, the Rates toolbar's three rows on
  a phone, a context message that repeats its field's name, touch sizes unchecked on an iPhone.
