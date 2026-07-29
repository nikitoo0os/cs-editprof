# Known issues

- No HLAE installation or CS2 test demo was available on the development machine, so real E2E rendering is not claimed.
- The unattended HLAE custom-loader CLI is confirmed from upstream source and used with `-insecure` plus isolated `USRLOCALCSGO`.
- `mirv_streams`, `mirv_cmd`, and `mirv_fov` are documented for
  AfxHookSource2. The NetCon sequencing of `playdemo`, `demo_gototick`, and
  first-person AccountID locking, with SteamID64 conversion and
  post-selection verification, has automated tests but still
  requires repeated real-machine E2E acceptance.
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
- Stage 8 contains placeholder catalogs for `de_mirage`, `de_inferno` and
  `de_dust2`, but none has calibrated safe camera volumes or a passed manual
  HLAE spike. They are therefore unsupported for non-POV shots and fall back
  to the selected player's POV.
- Camera capability discovery, command dispatch, low-resolution preview and
  retry are modelled fail-closed, but this development machine had no HLAE or
  CS2 installation. A real campath render, preview-quality result and high-FPS
  camera capture are not claimed.
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
