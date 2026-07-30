# Stage 8.1 camera acceptance

Accepted on 2026-07-30 for the target Windows render machine and the
`de_dust2` upper-tunnel profile.

## Environment

- HLAE `2.191.1` with AfxHookSource2 `0.40.1`
- CS2 launched through the HLAE custom loader with `-insecure`
- FFmpeg / FFprobe `n8.1.1`
- demo `match1.dem`
- selected player SteamID64 `76561199031052443`

The environment verifier passed Windows, HLAE, hook DLL, CS2, Steam, FFmpeg,
FFprobe, demo repair, NetCon port, process ownership, working-root,
interactive-session and automation checks.

## Installed command contract

The Render Agent probes the installed build after the demo reaches the active
game loop and archives `hlae-camera-command-report.json`. The accepted build
reported all required commands:

- `mirv_campath`
- `mirv_camio`
- `mirv_input camera`, `position`, `angles` and `fov`
- `mirv_fov`
- `mirv_cmd`
- `mirv_streams`

Non-POV jobs require the accepted HLAE version prefix, verification identity,
map name and calibrated safe volume.

## Static repeat-seek spike

Two 1280x720, 30 FPS clips were rendered in one shared CS2 session. The second
clip reused the loaded demo, sought back to the same warmup tick and applied
the same free-camera transform.

- duration: `7.866667` seconds each
- decoded frames: `236` each
- black frames, wall intersections and teleports: none observed
- camera: inside the accepted upper-tunnel volume
- repeat result: matching composition and event timing

## Four-keyframe campath

The accepted 1280x720, 60 FPS preview contains `470` decoded frames over
`7.833008` seconds. Before recording, the Render Agent:

1. seeks to each keyframe tick;
2. applies position, rotation and input-camera FOV;
3. reads the transform back from HLAE;
4. adds the keyframe;
5. parses `mirv_campath print` and compares all four transforms;
6. seeks back to the warmup tick;
7. ends input-camera mode and enables campath.

The four accepted ticks are `30150`, `30316`, `30482` and `30649`. Position,
rotation and FOV in the HLAE printout match the locked job values within
`0.02`.

## Final decoded evidence

`C:\Cs2Highlight\stage8-1-evidence\final\stage8-1-final.mp4`

- duration: `29.383333` seconds
- video: H.264, 1280x720, 60 FPS, 1763 frames
- audio: AAC stereo
- file size: `21,135,632` bytes
- full video/audio decode: passed
- black/freeze detector events: none
- audio mean / peak: `-15.6 dB` / `-4.6 dB`
- shot coverage: POV, static free camera and moving campath

## Accepted limitation

The bottom CS2 demo playback strip remains visible unless the operator presses
physical `Shift+F2`. `cl_showdemooverlay 0` is confirmed, while synthetic
keyboard input is filtered. This UI strip is explicitly excluded from Stage
8.1 acceptance; every other camera requirement above passed.

Stage 8.1 is accepted only for the recorded HLAE version and calibrated
`de_dust2` volume. Other versions, maps and volumes remain fail-closed.
