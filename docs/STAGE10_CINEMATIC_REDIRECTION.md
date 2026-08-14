# Stage 10: Cinematic re-direction

This stage extends the persisted Stage 8/9 pipeline. It does not introduce a
second editor or render path. `InteractiveTimelineDirector` owns user anchor
revisions, `CinematicDirector` builds the film plan, and the existing render
worker validates, renders, recovers, compiles, and publishes the result.

## Architecture

### Music and anchors

Music Analyzer `0.3.0` emits music-analysis schema `2.1`. The selected excerpt
contains a 160 Hz mono min/max envelope (waveform schema `1.0`). Web persists it
as `real-waveform-envelope.json`; the timeline draws those samples on a canvas
and shows an explicit unavailable state when the artifact cannot be validated.
It never synthesizes decorative samples.

An anchor mutation creates timeline revision data and replans only the regions
adjacent to the changed anchor. `LocalTimelineRegionPlan` schema `2.0`, planner
`10.1-local.1`, records its anchor bounds, music bounds, source materials,
highlight, B-roll, cameras, transitions, retiming, audio, effects, validation,
seed, and reuse outcome. Unaffected successful regions keep their persisted
plan and artifact references. Locked primary kills remain at their exact user
times.

### Gameplay and source selection

Demo Analyzer schema `1.3` samples every player needed by camera planning:
team, transform, velocity, weapon and firing/reload/utility/scope/bomb state.
Kill events include shooter and victim positions. Exact impact data is marked
unavailable when the parser cannot prove it, so bullet-path shots fail closed
instead of inventing a trajectory.

Selected-player kills also produce occasional victim-reaction candidates. They
begin shortly before impact, follow the real victim trajectory through the
death follow-through, use a tighter FOV, and are eligible only immediately
after the matching highlight. They never substitute an unrelated death.

Gap selection is content-driven: player approach/follow-through, group motion,
team setup, utility, weapon action, bomb action, establishing context, then POV.
Source reuse compares actual tick-range overlap inside the same demo, not only
string identity. When meaningful material is insufficient, the excerpt is
shortened; padding with repeated source, random fly-bys, frozen frames, or black
frames is not permitted.

### Camera planning and validation

Cinematic plan schema `2.0`, planner `10.1`, separates candidate generation,
safety filtering, editorial/diversity scoring, final selection, preview
validation, and fallback. The camera library represents static tripod, side,
rear and front tracking, group-wide, orbit, weapon-detail, exact bullet-path,
and POV families with subjects, source interval, keyframes, targets, FOV,
framing intent, direction, safe volume, fallback, and deterministic signature.

Non-POV shots remain fail-closed behind the verified map/HLAE profile and safe
volume. Preview media is analyzed with FFmpeg signal statistics and the planned
geometry is checked for framing, motion and subject visibility. A failed
preview is rendered again as POV. The resolved fallback output and status are
persisted and reused during recovery, including after process restart.

Tracking cameras follow the sampled player rail rather than a straight chord
between its endpoints. The planner deterministically alternates left/right
operator sides, tries progressively tighter offsets, samples every segment with
clearance inside calibrated cells, rejects restricted volumes, and only anchors
point B to the next highlight when that move is locally reachable. HLAE position
interpolation is linear so a cubic spline cannot overshoot the validated rail;
rotation remains smoothly interpolated. This calibration is conservative and
does not claim access to Source 2 collision polygons from a `.dem` file.

No automatic preview metric is claimed to be artistic or computer-vision
acceptance. A real CS2/HLAE render must still be watched in full.

### Effects, audio and final acceptance

Ordinary shots remain clean. Motivated peak/final/bass/drop treatments pass
through a rarity policy: lens warp is 80–150 ms, at most twice per short film,
and rare effects cannot land on adjacent kills. The final strong moment may use
hit-stop; strong bass may use lens warp; strong peaks may use recoil; drops may
use punch zoom. Determinism comes from content and revision-aware seeds, not a
mechanical effect rotation.

Music gain remains continuous around kills. The mix artifact records a stable
music envelope separately from the transient-shaped gameplay envelope. Final
compilation scans frame continuity, black/duplicate bursts, luminance spikes,
one-frame segments, and transition boundaries. A sampled FFmpeg detector rejects
a final MP4 when the CS2 demo playback strip is visible. NetCon reapplies the
clean presentation state after demo load and before capture operations.

## Persisted artifacts

The pipeline emits the following evidence from the checks it actually ran:

- `real-waveform-envelope.json`
- `local-region-plans.json`
- `camera-shot-candidates.json`
- `camera-shot-selection-report.json`
- `camera-shot-diversity-report.json`
- `camera-preview-quality-report.json`
- `source-interval-reuse-report.json`
- `effect-rarity-report.json`
- `transition-boundary-report.json`
- `frame-continuity-report.json`
- `music-gain-envelope.json`
- `gameplay-audio-envelope.json`
- `demo-ui-detection-report.json`
- `excerpt-extension-report.json`
- `cinematic-acceptance-report.json`

Reports distinguish analyzed, unavailable, fallback, warning, and rejected
states. A command being sent or a plan being written is not treated as proof
that pixels passed acceptance.

## Compatibility and migration

- Demo-analysis readers continue to accept schemas `1.0` through `1.3`.
- Music readers continue to accept schemas `1.0`, `2.0`, and `2.1`; only `2.1`
  can provide the real waveform artifact.
- Cinematic and local-region additions are serialized JSON fields/artifacts on
  existing entities. No relational column was added, so no EF migration is
  required.
- Older generation records remain readable. A missing waveform is shown as
  unavailable, and missing professional camera data falls back to existing POV
  behavior.
- Paid plans remain immutable. Recovery validates planner versions and reuses
  the same locked plan and persisted fallback resolution.

## Acceptance boundary

Unit, integration, synthetic FFmpeg, and Playwright tests verify contracts,
determinism, local DOM updates, fallback/recovery behavior, and technical media
invariants. They are not artistic acceptance. Completion of this stage still
requires a separately recorded production E2E run using a real demo, production
music, the verified `de_dust2` CS2/HLAE environment, and a full manual review of
the resulting MP4. If that run has not happened, no generation id, camera
family/signature list, music measurements, or artistic pass should be reported
as though it had.
