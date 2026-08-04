# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Primary users are Counter-Strike 2 players and creators who have recorded
match demos and want to turn selected moments into a finished highlight video.
Authenticated users can upload demos, choose a player and highlights, add or
analyze music, configure a cinematic timeline, pay with tokens, and download the
result. Administrators manage users, tokens, and operational visibility.

## Product Purpose

CSHighlighter turns raw CS2 demo material into a structured, music-driven
highlight movie. Success means a user can understand the workflow quickly,
make confident selections, see authoritative progress, and receive a usable
final video without losing ownership or visibility of their work.

## Positioning

The product's differentiator is an end-to-end workflow that combines demo
analysis, highlight selection, music-aware editing, cinematic timeline planning,
rendering, and verification in one owned generation.

## Operating Context

The web interface is used on a Windows desktop alongside Counter-Strike 2
capture and render tooling. Users move through upload, analysis, selection,
music, timeline, payment, rendering, and delivery states. The application has
authenticated user, admin, and legacy-generation access boundaries.

## Capabilities and Constraints

- ASP.NET Core Razor Pages with SQLite, Identity, SignalR, local workers, and
  token ledger services.
- The generation flow exposes progress, stage state, highlights, music, video,
  timeline editing, payments, profile, referrals, and admin controls.
- Existing functionality, routes, server-side validation, ownership rules,
  token accounting, legal pages, and local development workflow must remain
  intact during the redesign.
- The interface must remain usable on desktop and narrow screens, with keyboard
  access, visible focus, semantic controls, and reduced-motion compatibility.

## Brand Commitments

The product name is CSHighlighter / CS2 Highlight Generator. Existing copy is
Russian-first and should remain understandable to Russian-speaking users.
The product currently uses a dark, red-accented visual identity; this redesign
may replace the visual treatment while preserving the product name and tone.

## Evidence on Hand

The repository contains the working Razor UI, CSS token layer, SVG icon assets,
generation stepper, auth/profile/purchase/admin pages, and local quickstart
workflow. No external customer testimonials, brand photography, or verified
commercial claims are available; the redesign must not invent them.

## Product Principles

- Make the next useful action obvious.
- Treat a generation as owned work with visible state.
- Show progress as a trustworthy production pipeline, not decoration.
- Keep powerful editing controls understandable at a glance.
- Preserve user control, recoverability, and clear failure paths.

## Accessibility & Inclusion

The web UI must support keyboard navigation, visible focus, semantic labels,
high-contrast state indicators, responsive layouts, and a reduced-motion mode.
Russian copy should remain legible at normal desktop and mobile text sizes.

## Open Decisions

- Final visual direction and typeface are inferred for this redesign and can be
  revised by the product owner after the first visual pass.
- No new commercial, performance, or customer claims are authorized.
