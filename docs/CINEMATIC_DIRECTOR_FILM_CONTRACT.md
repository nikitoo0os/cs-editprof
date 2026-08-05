# Cinematic Director film contract

This document defines production invariants for the `Cinematic Director`
style. They are acceptance rules, not optional planner preferences.

## Timeline and source material

- POV footage is allowed only inside a selected highlight and must contain the
  relevant combat payoff. Running, aiming or firing without the selected kill
  is not valid filler.
- Every non-highlight insert must use a preview-validated free-camera or tripod
  shot. A rejected or unavailable camera route must never be silently replaced
  by POV. The planner must select another cinematic route; Auto duration may
  shorten the excerpt, while an explicit duration fails closed with a precise
  camera/material error.
- A free-camera/tripod insert is at least 1.5 seconds long. Residual shorter
  gaps are absorbed by adjacent shot boundaries or retiming; they do not become
  micro-shots.
- Transition routes use one smooth `A -> B` move without an unrelated third
  destination. When a following highlight exists in the same demo, `B` is
  anchored to that highlight's subject/location so the flight hands the story
  into the next action.
- Selected kills are spread across the whole movie. The planner must avoid a
  front-loaded block of kills followed by a long block of B-roll and must keep
  chronology whenever the available musical anchors allow it.

## Music, speed and effects

- Slow motion on a highlight is motivated by a sharp music-energy/onset change
  and is localized to the firing/impact window immediately around a selected
  kill. Running and uneventful POV are not slowed to manufacture duration.
- Occasional jump slow motion is allowed in a verified free-camera shot.
- Kill treatments use the motivated-effect policy and retain visible variety
  across the film. Effect application is verified from the compiled result;
  an effect name in the plan alone is not acceptance evidence.

## Reference-derived editorial grammar

The supplied reference is a music-led fragmovie, not a continuous free-camera
showreel. Its grammar is the invariant to reproduce; the reference timecodes
below are observations, not timestamps to hard-code into every generated movie.

- The opening is a cold open: approximately 5-7 seconds of source-cinematic
  inserts precede gameplay. It uses a hard black lead-in, a monochrome
  letterboxed close-up of weapon and gas-mask, a low-angle foot/ground shot,
  a medium frontal character shot, a low-angle jump, a brief return to black,
  and a centered white title card (`THE END`). The title is a type card, not an
  outro and must not be inserted after the final kill.
- When the project B-roll budget is shorter than the reference cold open,
  compress this grammar into the allowed 2-3.2 second prelude by shortening
  or omitting inserts; never compensate by creating sub-1.5-second B-roll
  timeline segments or by filling the body with unmotivated free-camera.
- The gameplay body begins through a white-flash/overexposed hand-off and then
  alternates readable POV setup, scope punctuation, firing/impact, and a short
  post-kill exit. The edit is dense but not one-frame fast: hold setup long
  enough to establish the angle, then make the impact and the next weapon or
  location change land on a musical onset.
- Sniper scope is a diegetic scope insert, not a generic overlay. Keep the
  scope only when it contains aiming, the shot, or the immediate payoff; never
  use an empty scope as filler. A scope opening/closing may be the visual
  punctuation for a sniper kill, while the kill proof remains visible.
- Weapon changes are editorial punctuation. AWP/sniper, rifle, pistol and
  knife views may be intercut when the action or musical accent justifies the
  change; do not cut to an unrelated weapon merely to create variety.
- The reference has no sustained gameplay free-camera run. Its non-POV
  language is concentrated in the opening: low-angle/ground-level, close-up,
  lateral or diagonal tracking, and elevated/jump perspective. In this
  project, each such route still has to pass the preview gate and the existing
  1.5 second B-roll floor. If a source contains several shorter internal
  cutaways, they are one validated intro sequence asset; do not model them as
  sub-1.5-second timeline B-roll segments.
- Gameplay remains full-frame 16:9. Letterbox is reserved for the validated
  cinematic prelude or an explicitly cinematic insert and must be removed
  before the combat timeline resumes; never crop the HUD to manufacture a
  cinematic aspect ratio.

## Reference-derived effect vocabulary

Use the following terms in plans, warnings, reports and tests. The term names
are semantic contracts, not invitations to stack every effect on every kill.

- `HardCut`: a frame-accurate direct cut with no generated transition frame.
- `FlashCut` / `white-flash`: a short white exposure pulse at an impact,
  musical onset, or section hand-off. It is normally 2-5 frames at 30 fps
  (about 67-167 ms), peaks briefly, and returns to a readable frame; it is not
  a permanent white grade.
- `WhipPan`: a cut whose outgoing/incoming direction is bridged by a fast
  pan-like smear. `DirectionalMotionBlur` is the temporal blur component;
  `WhipZoom` is the variant that also changes scale. Use these only when the
  motion direction or action supplies motivation.
- `ZoomBlur`: short radial/optical blur toward the focal point, used around a
  scope snap, impact or transition. It is distinct from directional blur and
  must not remain on the whole shot.
- `RgbSplit` / `chromatic aberration`: a brief red/green/blue channel offset,
  often paired with a small displacement or glitch smear. The reference uses
  it as a rare peak accent (visible around 28.7 s and 52.5 s), not as a base
  look. Do not repeat it on adjacent shots; leave at least 8 seconds between
  unrelated uses unless a single continuous climax requires otherwise.
- `FrameEcho`: a short temporal after-image/strobe echo around a hit. It may
  accompany `RgbSplit`, but the two together count as one peak treatment and
  must not obscure the kill silhouette.
- `FlashAccent`: a localized pulse on the impact frame; it is a kill accent,
  not the same thing as a full outgoing `FlashCut` transition.
- `VignettePulse`: a brief edge-darkening pulse that directs attention to the
  center. A scope's native circular mask is not itself a `VignettePulse`.
- `HitStop`: a localized freeze or near-freeze at the confirmed impact. It
  must never move the locked kill timestamp or duplicate a kill.
- `LensWarpPulse` and `RollBurst`: optional one-beat distortion/rotation
  accents for a peak or transition. They are prohibited on calm setup shots
  and may not be stacked with more than one other high-cost distortion.
- `Desaturation` / `monochrome grade` is a shot-level grade for the cold open
  or a deliberately selected climax passage. It is not a substitute for
  `RgbSplit`, and it must not desaturate the entire movie by accident.

Effect selection is ordered: first preserve action readability, then choose
one transition treatment, then at most one compatible impact accent. A single
frame may not contain an opaque white flash, full-screen blur, RGB split,
freeze and rotation simultaneously. Every non-trivial effect records its
anchor (`music_onset`, `fire`, `impact`, `weapon_swap`, `round_boundary` or
`camera_transition`), duration in frames, parameters, and compiled-output
evidence.

## Reference-derived picture treatment

- The cold open is monochrome/low-saturation with controlled filmic contrast,
  shallow-looking close-up composition, and horizontal letterbox bars. Prefer
  subject isolation: muzzle or mask in the foreground, feet crossing the low
  frame, or a character entering a strong diagonal.
- Normal gameplay is bright and high-key, with lifted whites and a saturated
  weapon accent. White flashes may briefly approach overexposure, but the
  normal frame must retain map texture, player silhouette and killfeed
  readability. Do not use a global blown-out grade as a replacement for a
  flash transition.
- Motion blur is localized to a whip, fast turn, weapon swap or impact. A
  static hold, scope read or post-kill confirmation must resolve to sharp
  enough detail to prove what happened.
- The grade may progress from subdued/monochrome intro to brighter,
  contrast-rich combat and then fall away in the closing passage. The report
  must name the applied grade and the sections where it changes.

## Reference-derived audio treatment

- The reference is music-led: there is no voice-over or dialogue anchor, and
  the continuous track supplies the structure. Cuts, flash accents, scope
  snaps and weapon changes are aligned to a detected beat/onset or to the
  verified fire/impact event when the two coincide.
- The source mix has a short fade-in (about 0.3 seconds), a short fade-out
  (about 0.5 seconds), and an intentionally aggressive loudness profile
  (measured at approximately -6.3 LUFS integrated and +1.9 dBTP true peak).
  Those source values are descriptive only; the generated deliverable must
  use the safer project target of -14 LUFS integrated, LRA 7 LU, and -0.8 dBTP
  maximum true peak. No final render may clip.
- `MusicOnset` means a measured beat/transient or section-energy change, not a
  guessed timestamp. The plan stores onset time in source and output time so
  retiming can be verified. A kill accent may lead or trail the onset only
  within the configured alignment tolerance.
- If game audio is retained, it is subordinate to the music during the dense
  montage: duck the bed around the selected fire/impact and restore it with a
  short release. Never let an incidental chat line, spectator hint or menu
  sound become the dominant editorial event.

## Picture and HUD

- Cinematic Director uses a visibly saturated, contrast-rich final grade. The
  selected narrative grade and the strong cinematic finish must both appear in
  the compilation report.
- Gameplay POV keeps combat readability. Cinematic free-camera/tripod shots use
  the cinematic HUD profile.
- The chat/message block, spectator hints and the centered spectator-player
  panel are hidden. The killfeed is filtered to the selected player's kills
  when the capture integration supports it.

## Required validation

Before compilation, the locked plan must satisfy all of the following:

1. no B-roll segment has `PlayerPov` type or family;
2. every B-roll segment is at least 1.5 seconds;
3. every free-camera/tripod segment passed the current preview gate;
4. no persisted POV fallback is reused as cinematic material;
5. output kill positions and their effects remain locked after retiming;
6. there are no unfilled gaps, black transition frames or one-frame segments.
7. every transition and accent has a semantic anchor, duration in frames and
   compiled-output evidence;
8. no adjacent shots repeat `RgbSplit`, and no cinematic effect stack obscures
   the selected kill or scope proof;
9. gameplay is restored to full-frame 16:9 after any letterboxed prelude;
10. final audio measures -14 LUFS integrated, LRA 7 LU and no higher than
    -0.8 dBTP true peak, with verified start/end fades.

The planner version implementing this contract is `10.8` or newer.

## Executable enforcement

- `CinematicContractPolicy` is the single acceptance policy for this style.
  `ComposeAsync` validates the locked plan before invoking FFmpeg and fails
  closed with a `CINEMATIC_CONTRACT_*` error when a rule is violated.
- The render gate writes `cinematic-contract-plan-report.json` before media
  compilation and `cinematic-contract-render-report.json` after final-media
  verification. The latter records the measured loudness, LRA, true peak,
  duration, frame-continuity result and verified dynamic-effect clips.
- A successful Cinematic Director render must have a continuous timeline, a
  rendered duration within two frames of the locked target, all planned
  effect clips verified, and audio within the limits in this document. A
  camera-only preview is an auxiliary diagnostic variant, not the final film.
- Timeline markers are intent inputs, not fragile coordinates. Out-of-range
  times are clamped, an unavailable or already assigned exact highlight is
  reassigned to the best compatible free highlight, and an impossible extra
  marker is removed with a `markerAutoRepair` diagnostic. Explicitly locked
  anchors remain protected and may still cause a precise validation error when
  they make the contract physically impossible.
- The `Seconds60` duration selection is the minute-long mode. It is capped by
  the same 60-second contract maximum and cannot bypass the cinematic checks.
