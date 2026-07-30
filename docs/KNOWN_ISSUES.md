# Known issues

- HLAE 2.191.1 / AfxHookSource2 0.40.1, CS2, NetCon and FFmpeg were exercised
  on the target Windows render machine with `-insecure` and isolated
  `USRLOCALCSGO`. Other HLAE or CS2 versions remain unverified.
- Current CS2 clients can reject legacy entity message type 138 in otherwise
  valid demos. The agent applies the narrow, fail-closed
  `cs2-demo-playback-fix` v0.1.1 rewrite to its isolated copy before playback.
  This does not guarantee compatibility with every historical demo format.
- The output watcher requires a stable MP4 and validates duration, video stream,
  width, and height through ffprobe before publishing `raw-highlight.mp4`.
- The pinned parser exposes wallbang, no-scope, through-smoke, distance,
  WeaponFire and killer health. `oneTap` is a best-effort WeaponFire-derived
  value. Reliable last-enemy and reaction-time signals are unavailable, remain
  unset, emit parser warnings and receive no BeautyScore.
- The `capture-gameplay-clean.v2` command adapter and effect filter graphs have automated
  tests, but clean captured pixels and visual effect timing still require the
  real Stage 5 checklist on the installed CS2/HLAE/FFmpeg versions.
- Process ownership cleanup is limited to the process tree started by `ProcessSupervisor`; no broad name-based killing is performed.
- Stage 8.1 has one calibrated non-POV profile: the `de_dust2` upper-tunnel
  safe volume documented in `STAGE8_1_ACCEPTANCE.md`. `de_mirage`,
  `de_inferno` and every unverified Dust2 volume remain POV-only.
- The CS2 demo playback control strip can remain visible even when
  `cl_showdemooverlay 0` is confirmed. Physical `Shift+F2` hides it on the
  accepted machine, but synthetic input is filtered by CS2. Stage 8.1 was
  accepted with this explicit pixel-cleanliness exception.
- A forced CS2 cleanup after a failed HLAE camera calibration can leave Steam
  unable to start the next injected session until Steam is restarted. Normal
  completed sessions exit cleanly; one early NetCon startup retry is automatic.
- Stage 8 frame/section classification has deterministic unit coverage. Full
  `librosa` analysis must still be rebuilt with Python 3.10/3.11 and tested
  against the production music file; low-confidence sections are intentionally
  reported rather than promoted to Drop.
- The demo parser exposes movement, alive/freeze state and nearby gameplay
  actions, but it does not prove smoke occlusion, wall visibility or captured
  loading/UI pixels. B-roll candidates therefore still require the clean
  capture profile plus real visual review.
- `cinematic-alignment-report.json` records planned timing with
  `VerifiedFromRenderedMedia=false`. FFprobe plus manual visual/audio review of
  the final gameplay MP4 is required before claiming kill-to-peak alignment,
  unclipped audio, clean HUD or professional motion quality.
