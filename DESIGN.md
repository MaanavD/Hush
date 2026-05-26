# Design: Hush

## Register
product

## Color Tokens

### Background
- `Hush.Bg`: near-black, blue-tinted — `#0D0D16`
- `Hush.Surface`: slightly elevated surface — `#13131F`
- `Hush.Border`: subtle border — `#1C1C2C`

### Accent (raw mode, violet-purple)
- `Hush.Accent`: primary interactive accent — `#9B7FEB`
- `Hush.Accent.Dim`: selected state, pressed state — `#4B3E8A`

### Clean mode (amber)
- `Hush.CleanMode`: amber for clean-mode sessions — `#F59E0B`
- `Hush.CleanMode.Dim`: pressed/active clean-mode — `#B45309`

### Semantic
- `Hush.Recording`: active recording indicator — `#E05757`
- `Hush.Error`: error state — `#C94040`

### Text hierarchy
- `Hush.Text.Primary`: primary readable text — `#EEEEF8`
- `Hush.Text.Secondary`: secondary and descriptive text — `#7A7A98`
- `Hush.Text.Muted`: hints, metadata, structural labels — `#35354E`

## Typography
- Stack: Segoe UI Variable Text, Segoe UI, Helvetica Neue, Cantarell, Ubuntu, sans-serif
- Scale: 9 / 10 / 11 / 12 / 13 / 18 px (fixed, density-optimized for desktop UI)
- Section labels: 9px Bold LetterSpacing=2, ALL-CAPS (structural signals, not headings)
- Body labels: 13px primary, 10px secondary below for helper text
- Scale ratio: 1.1-1.2 between adjacent steps (tight, product-appropriate)

## Spacing
- Section-to-section gap: 24px
- Control-to-control within section: 10px
- Header/footer padding: 28px horizontal, 22px vertical
- Content area horizontal margin: 28px

## Component patterns

### Overlay pill
Light frosted glass (#F0FFFFFF) is intentional: the overlay must be legible on any underlying
app. CornerRadius="16" gives a soft, unobtrusive shape. Subtle BoxShadow grounds it without
drama. No backdrop blur (performance-first for a background utility).

### Settings rows
Two-column grid: 160px label column + * content column. Label area shows the setting name at
13px + a 10px helper below. No card wrapping: flat rows are cleaner and denser.

### Section headers
9px Bold ALL-CAPS with LetterSpacing="2". The INPUT/MODEL/BEHAVIOR sections use
`Hush.Text.Secondary`. The CLEAN MODE section uses `Hush.CleanMode` to signal its mode.

### ListBox (prompt picker)
CornerRadius="8" per item. Selected background uses `Hush.Accent.Dim` (darker purple) for
better contrast with primary text. Item borders disappear on selection so the accent fill reads
cleanly.

## Motion
State transitions: 150ms ease-out. No choreography: all transitions are imperceptible
background activity. The overlay should appear and disappear with minimal fanfare.
