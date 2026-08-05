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

The planner version implementing this contract is `10.7` or newer.
