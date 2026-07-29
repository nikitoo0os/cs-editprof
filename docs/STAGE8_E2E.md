# Stage 8 real E2E

Stage 8 is accepted only after a real HLAE/CS2/music/browser run on the target
Windows render machine. Unit tests, synthetic FFmpeg fixtures and planned JSON
alignment are useful diagnostics, but are not evidence of camera quality,
perceived musical sync or clean gameplay pixels.

## 1. Build parsers, analyzer and solution

```powershell
git pull
.\scripts\build-demo-parser.ps1
.\scripts\build-music-analyzer.ps1 -PythonVersion 3.11
dotnet build .\Cs2Highlight.RenderPoC.sln -c Release
dotnet test .\Cs2Highlight.RenderPoC.sln -c Release --no-build
```

The parser output must use schema `1.2`, contain timeline frames for the
selected SteamID and have non-empty trajectories. Music analysis must use
schema `2.0`, a frame hop between 20 and 50 ms, detailed sections and musical
peaks.

## 2. FFmpeg cinematic fixture

```powershell
$env:CS2_STAGE8_FFMPEG = "C:\path\to\ffmpeg.exe"
$env:CS2_STAGE8_FIXTURE_OUTPUT = "$PWD\artifacts\stage8-fixtures"
dotnet test .\tests\Cs2Highlight.Web.Tests -c Release `
  --filter "Category=Stage8Ffmpeg"
```

This fixture must produce an 8-second probed MP4 and verified dynamic-effect,
audio-mix, alignment, color and compilation reports. It validates the
executable composition graph, not real gameplay or artistic quality.

## 3. Manual HLAE camera spike

Before enabling a non-POV camera, record the installed HLAE and CS2 versions
and inspect the built-in help for:

```text
mirv_campath
mirv_camio
mirv_input
mirv_fov
mirv_cmd
mirv_streams
```

Use an isolated `-insecure` CS2 launch. On one supported map, manually create a
four-keyframe campath, play it through the intended tick range and record a
low-resolution preview. Verify:

- the commands are accepted by the installed AfxHookSource2 build;
- position, rotation, FOV and timing are stable after demo seek;
- the camera stays inside a manually calibrated safe volume;
- there are no black frames, teleports, walls or abrupt FOV jumps;
- the exact same commands work after a second demo seek.

Only then may the profile be marked `ManuallyVerified` and its calibrated safe
volumes committed. An empty or unverified profile must remain POV.

## 4. Start Web and run the browser flow

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5080"
dotnet run --project .\src\Cs2Highlight.Web -c Release --no-build
```

In another terminal:

```powershell
$env:CS2_STAGE8_BROWSER_E2E = "1"
$env:CS2_WEB_BASE_URL = "http://127.0.0.1:5080"
$env:CS2_STAGE6_MUSIC = "D:\media\production-track.mp3"
$env:CS2_STAGE6_DEMOS = "D:\demos\one.dem;D:\demos\two.dem"
$env:CS2_STAGE6_STEAM_ID = "76561198000000000"
dotnet test .\tests\Cs2Highlight.Web.Tests -c Release `
  --filter "Category=Stage8BrowserE2E"
```

The test selects Cinematic Director, Auto duration, Balanced edit intensity
and the fail-closed POV camera path, completes checkout, waits through SignalR
updates without manual reload, plays the final result and downloads the MP4.
Run a separate manual-camera acceptance only after section 2 passes.

## 5. Required artifact checks

Archive:

- `music-analysis.json`;
- `cinematic-music-narrative.json`;
- `cinematic-movie-plan.json`;
- `cinematic-alignment-report.json`;
- `camera-capabilities.json`;
- per-demo `batch-plan.json`, `batch-state.json` and `batch-report.json`;
- compilation/audio/alignment/color/effect results;
- `generation-report.json`;
- final MP4 and relevant terminal logs.

Confirm from the locked plan:

1. The selected excerpt is contiguous and contains enough allowed peaks.
2. Every primary kill is in Drop, Chorus or HighEnergy.
3. The timeline starts at zero and has no gap or overlap above 50 ms.
4. If total highlight material is under 15 seconds, total output is at most
   30 seconds and B-roll does not exceed highlight duration.
5. Every B-roll tick range is outside selected highlight ranges.
6. Each segment refers to an actually rendered source.
7. One demo produces one batch/CS2 session containing all of its highlight and
   B-roll jobs.
8. High-FPS capture is used only for a validated cinematic/retimed shot and is
   capped by the plan.

## 6. FFprobe and manual review

Probe the final file and every camera preview:

```powershell
ffprobe -v error -show_streams -show_format `
  "D:\Cs2Highlights\web\generations\<id>\output\final-highlights.mp4"
```

Watch the complete MP4 with sound. Record actual:

- output duration and file size;
- B-roll/highlight/campath/high-FPS shot counts;
- average and maximum perceived/decoded kill-to-peak error;
- black-frame, clipping, HUD, killfeed and loading-overlay findings;
- browser playback and download result;
- preview failures, retries and every POV fallback.

Update the alignment evidence only from decoded/rendered media. Do not change
`VerifiedFromRenderedMedia` based on the planned JSON alone.

## 7. Recovery

Stop Web once while rendering sources and once during composition. After each
restart, verify that:

- the locked cinematic and effect plans are unchanged;
- completed batch items are reused;
- a POV camera-stage recovery follows the persisted fallback;
- an unverified non-POV camera recovery fails for manual review;
- the final timeline and peak assignments remain identical.

Until every applicable item passes, report Stage 8 as implemented and
automatically tested with POV fallback, not as a completed real cinematic
HLAE/CS2 E2E.
