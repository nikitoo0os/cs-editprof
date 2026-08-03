---
name: CSHighlighter Beatgrid Control Room
description: A measured production console for turning CS2 demos into highlight movies.
colors:
  graphite: "#090b0e"
  graphite-raised: "#101419"
  control-surface: "#141a20"
  control-surface-strong: "#1a2229"
  line: "#2a353d"
  line-strong: "#40515b"
  warm-text: "#f2efe8"
  soft-text: "#c0c8ca"
  muted-text: "#839097"
  signal-orange: "#ff623d"
  signal-orange-deep: "#d8432b"
  data-cyan: "#71d8d4"
  state-green: "#98d6a5"
  state-yellow: "#e6c56b"
typography:
  display:
    fontFamily: "ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "clamp(2.8rem, 5.8vw, 5.35rem)"
    fontWeight: 850
    lineHeight: 0.94
    letterSpacing: "-0.06em"
  body:
    fontFamily: "ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "17px"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: "SFMono-Regular, Consolas, Liberation Mono, monospace"
    fontSize: "10px"
    fontWeight: 700
    lineHeight: 1.2
    letterSpacing: "0.08em"
rounded:
  sm: "8px"
  md: "10px"
  lg: "14px"
spacing:
  xs: "5px"
  sm: "10px"
  md: "16px"
  lg: "24px"
  xl: "36px"
components:
  button-primary:
    backgroundColor: "{colors.signal-orange}"
    textColor: "#1b100d"
    rounded: "{rounded.sm}"
    padding: "0 16px"
    height: "44px"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.warm-text}"
    rounded: "{rounded.sm}"
    padding: "0 16px"
    height: "44px"
  input:
    backgroundColor: "{colors.graphite-raised}"
    textColor: "{colors.warm-text}"
    rounded: "{rounded.sm}"
    padding: "12px 14px"
  surface:
    backgroundColor: "{colors.control-surface}"
    textColor: "{colors.warm-text}"
    rounded: "{rounded.lg}"
    padding: "36px"

# Design System: CSHighlighter Beatgrid Control Room

## Overview

**Creative North Star: "Beatgrid Control Room"**

CSHighlighter is designed as a production console for an editor working late
with a match demo, a track, and a timeline. The interface is calm enough for
long selection sessions but has a clear signal language: orange means act,
cyan means measured data, and the mono labels describe the current lane.

The system uses tonal graphite surfaces, warm text, crisp borders, and compact
operator geometry. Depth comes from layered surfaces and ambient shadows, not
glass, blur, or decorative glow. The visual world should remain recognizable
when the content changes from upload to player selection, highlights, music,
timeline, admin, or delivery.

**Key Characteristics:**

- Production-lane reading order instead of generic dashboard chrome.
- Signal-orange actions and data-cyan status markers.
- Warm, high-contrast text on graphite or cool paper surfaces.
- Mono labels reserved for workflow, state, and measurement.
- Compact 8–14px geometry with 1px structural lines.

## Colors

The palette is restrained: graphite and cool paper establish the work surface,
orange carries action, and cyan carries measured progress and data.

### Primary

- **Signal Orange** (#ff623d): primary upload, submit, and current-action color.
- **Data Cyan** (#71d8d4): workflow data, completed state, focus-adjacent cues,
  and measured interface details.

### Secondary

- **State Green** (#98d6a5): successful or completed system state.
- **State Yellow** (#e6c56b): warning and attention state.

### Neutral

- **Graphite** (#090b0e): page background.
- **Graphite Raised** (#101419): controls, inputs, and secondary work areas.
- **Control Surface** (#141a20): primary content panels.
- **Control Surface Strong** (#1a2229): elevated panels and selected regions.
- **Warm Text** (#f2efe8): primary content and headings.
- **Soft Text** (#c0c8ca): supporting content.
- **Muted Text** (#839097): metadata and quiet guidance.
- **Structural Line** (#2a353d): dividers and resting borders.
- **Strong Line** (#40515b): interactive borders and field edges.

**The Signal Rarity Rule.** Orange is reserved for actions and current work;
cyan is reserved for data. Neither accent becomes general decoration.

## Typography

**Display Font:** system sans stack (`ui-sans-serif`, system-ui, Segoe UI)

**Body Font:** the same system sans stack

**Label/Mono Font:** SFMono-Regular, Consolas, Liberation Mono, monospace

**Character:** One dependable sans keeps the product familiar during operation;
the mono face creates the distinct control-room voice only where the interface
is naming a lane, state, count, or measurement.

### Hierarchy

- **Display** (850, `clamp(2.8rem, 5.8vw, 5.35rem)`, 0.94): home promise and
  major route headings.
- **Title** (850, `clamp(2.35rem, 4vw, 4.1rem)`, 0.94): page-level task title.
- **Section** (800, approximately 1.2–1.55rem): panel and workflow headings.
- **Body** (400, 15–17px, 1.65): explanatory copy, kept to a readable measure.
- **Label** (700, 10px, 0.08em, uppercase): workflow, state, and operational
  metadata only.

**The One Voice Rule.** Do not introduce a display serif or a second headline
family; hierarchy comes from scale, weight, and the signal vocabulary.

## Layout

The primary desktop container is a centered 1440px maximum with 28px gutters.
The home surface uses a two-column production bay: narrative and workflow on
the left, upload/input on the right. Auth and legal surfaces narrow to a single
focused measure. Generation surfaces keep the stepper as a horizontal command
rail and collapse it into an overflow-safe strip on smaller screens.

The spacing rhythm uses 10px for tight groups, 16px for control gaps, 24px for
panel breathing room, 36px for surface padding, and 62px-plus for page-level
separation. At 720px and below, grids collapse to one column, the home workflow
becomes a 2×2 lane map, and navigation becomes a horizontally scrollable rail.

## Elevation & Depth

Depth is hybrid but restrained: borders define structure, tonal shifts define
adjacency, and a soft ambient shadow separates major surfaces from the page.
The product does not use glass as decoration or hard offset shadows.

### Shadow Vocabulary

- **Surface ambient** (`0 18px 44px rgb(0 0 0 / 0.36)`): major panels that need
  separation from the control-room background.
- **Action lift** (`0 9px 20px rgb(255 98 61 / 0.16)`): primary action only.

**The Layer Before Shadow Rule.** Use a tonal surface or structural line first;
use a shadow only when the surface truly needs to lift from its parent.

## Shapes

Controls use a compact 8px radius, cards and work surfaces use 10–14px, and
large pills are avoided except for small balance or status controls. Borders are
1px and purposeful. Upload zones use a dashed 1px boundary because the user
needs to understand the drop target; ordinary cards do not use decorative side
stripes.

## Components

### Buttons

- **Shape:** compact 8px corners (8px), 44px minimum height.
- **Primary:** signal orange with dark text, 16px horizontal padding, and a
  restrained action shadow.
- **Hover / Focus:** lift by 2px on hover; visible cyan focus ring and border
  shift; reduced-motion removes the movement.
- **Secondary / Ghost:** transparent or tonal surface with strong structural
  border; cyan on hover.

### Chips

- **Style:** small 5px corners, mono uppercase labels, tinted background, no
  oversized pill treatment.
- **State:** orange for new/current work, cyan or green for data/completion,
  yellow for attention.

### Cards / Containers

- **Corner Style:** 10px for content cards; 14px for primary surfaces.
- **Background:** control surface or control surface strong, never a generic
  white card inside the dark world.
- **Shadow Strategy:** ambient shadow only on major surfaces; cards are flat at
  rest.
- **Border:** 1px structural line, strong line on interactive edges.
- **Internal Padding:** 20px compact, 36px primary surface.

### Inputs / Fields

- **Style:** graphite-raised fill, strong line, 8px radius, readable warm text.
- **Focus:** cyan border with a subtle 3px cyan ring.
- **Error / Disabled:** error uses muted red surface and text; disabled states
  reduce contrast and remove action lift without hiding the control.

### Navigation

The header is a sticky compact operator rail. The brand mark is a square orange
CH signal. Utility actions stay familiar, and the responsive rail collapses by
wrapping into a second row rather than hiding the primary route.

### Workflow Strip

The home workflow strip and generation stepper use mono lane labels, 1px
connectors, and one active orange index. Completed work is cyan. The pattern is
the signature bridge between the user's raw demo and the final movie.

## Do's and Don'ts

### Do:

- **Do** make the next operational action visible within the first viewport.
- **Do** use orange for actions and cyan for measured data.
- **Do** keep controls keyboard reachable with a visible focus treatment.
- **Do** let responsive layouts collapse structurally instead of shrinking
  type until it becomes decorative.
- **Do** use real workflow labels and product language in every state.

### Don't:

- **Don't** reintroduce gradient text, glass cards, or decorative grid texture.
- **Don't** use a second display font for a technical mood.
- **Don't** turn every panel into a same-size icon card.
- **Don't** use accent color on inactive content just to fill space.
- **Don't** let workflow lanes or timeline controls create horizontal page
  overflow on narrow screens.
