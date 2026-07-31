# Professional editing pass

This pass turns Cinematic Director from a peak-aligned clip concatenator into
a deterministic short-form editing system. It was derived from visual review
of two reference highlight edits and the project's latest generated MP4.

## Editorial structure

- When safe B-roll exists, up to 2-3.2 seconds are reserved for a non-combat
  intro without exceeding the global B-roll budget.
- Selected highlights are reordered as an escalation: smaller plays establish
  the sequence and the strongest multikill becomes the climax.
- Kills remain assigned to Drop, Chorus or HighEnergy sections. The intro,
  calm sections and the lead-in to the first drop are B-roll territory.
- The movie ends on the final hero shot. Missing music-section confidence
  cannot silently turn the remaining timeline into B-roll.
- Sub-750 ms musical gaps are closed by a bounded alignment adjustment instead
  of inserting distracting one-frame fly-throughs.

The director records narrative reflow and intro reservation decisions in the
movie-plan warnings so a generation remains reproducible and diagnosable.

## Shot treatment

Balanced and Strong modes no longer repeat one zoom stack on every kill. Seven
deterministic treatment families rotate across the selected cards without
adjacent repetition: clean recoil, crash zoom with optical blur, frame echo
with RGB separation, offset zoom with roll, lens warp, a long subtle push-in,
and hit-stop. Four of the seven families deliberately contain no zoom at all;
the last hero frag receives a separate five-layer climax treatment.

Each family uses its own timing window around the musical anchor. Zoom blur is
rendered spatially while directional motion blur remains temporal, so the two
no longer collapse into the same visual texture. Vignette accents breathe as a
short pulse instead of switching on as a flat overlay. Slow motion is deeper on
multikills, selective and subtle on solo kills, and absent from the remaining
shots; its local compensation keeps the kill on the intended beat.

Outgoing transitions are selected from scene metadata:

- flash/flashbang, sniper shots and selected headshots use FlashCut;
- smoke and peak-to-outro boundaries use FadeTransition;
- fast movement, weapon swaps and multikills use WhipPan;
- neutral shots rotate through hard cuts, WhipPan, FlashCut, FadeTransition
  and WhipZoom deterministically.

## Composition and sound

Cinematic Director preserves the complete requested 16:9 frame. It applies a
small contrast, saturation and gamma finish without cropping the HUD or adding
black bars. The intro-to-combat boundary uses a short white flash transition
instead of a black dip.

Color now progresses from a subdued intro through a neutral buildup to a
slightly brighter, higher-contrast climax, then falls away in the outro.
Gameplay becomes substantially more present during Drop, Chorus and HighEnergy
sections, while the music ducks briefly around kill accents and recovers over
280 ms. The final audio target is `-14 LUFS`, with `LRA=7` and a `-0.8 dBTP`
ceiling.

The catalog no longer asks for an arbitrary Top-3, Top-5 or Top-10 limit.
Every explicitly selected card is included. One-click recommendations select
up to 12 varied moments so the director has enough material for a real
fragmovie arc.

Batch rendering starts a fresh CS2 process for every clip by default. Reusing
one live demo session is faster, but repeated `demo_gototick` seeks can corrupt
Source 2 entity baselines and terminate playback with `CopyNewEntity: invalid
class index`. Shared-session mode is therefore opt-in until seek reuse is
proven safe for the installed game build.

## Verification

Run without starting Web:

```powershell
dotnet test .\Cs2Highlight.RenderPoC.sln --no-restore
dotnet build .\Cs2Highlight.RenderPoC.sln -c Release --no-restore
```

To exercise the real filter graph with the bundled FFmpeg:

```powershell
$env:CS2_STAGE8_FFMPEG = `
  ".\artifacts\stage7-tools\ffmpeg-8.1.2-essentials_build\bin\ffmpeg.exe"
$env:CS2_STAGE8_FIXTURE_OUTPUT = ".\.tmp\stage9-fixtures"
dotnet test .\tests\Cs2Highlight.Web.Tests `
  --filter "FullyQualifiedName~CinematicCompositionRendersAndProbesWhenOptedIn"
```

These checks prove plan and filter-graph integrity. Final artistic acceptance
still requires a new generation from real demos, listening on speakers and
headphones, and watching every camera cut for wall intersections and HUD
artifacts.
