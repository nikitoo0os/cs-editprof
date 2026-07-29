# Stage 7 real E2E

Stage 7 is complete only after both the deterministic FFmpeg fixture run and a
real CS2/HLAE generation pass on the target render machine. Synthetic fixtures
are deliberately not presented as proof that HUD, killfeed, camera motion and
weapon animation look correct.

## 1. Build and regular tests

```powershell
git pull
dotnet build .\Cs2Highlight.RenderPoC.sln -c Release
dotnet test .\Cs2Highlight.RenderPoC.sln -c Release --no-build
```

## 2. FFmpeg capability and fixture run

Use the same FFmpeg build configured for the Web pipeline:

```powershell
$env:CS2_STAGE7_FFMPEG = "C:\path\to\ffmpeg.exe"
$env:CS2_STAGE7_FIXTURE_OUTPUT = "$PWD\artifacts\stage7-fixtures"
dotnet test .\tests\Cs2Highlight.Web.Tests -c Release `
  --filter "Category=Stage7Ffmpeg"
```

Expected artifacts:

- `ffmpeg-capabilities.json`;
- `fixture-report.json`;
- one MP4 for each effect fixture.

Every fixture must pass ffprobe validation, remain within 3.90–4.10 seconds,
retain 1280×720 video and contain no black frame, invalid crop or broken
timeline. Review the MP4s visually; test success alone validates structure,
not motion-design quality.

## 3. Start Web

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5080"
dotnet run `
  --project .\src\Cs2Highlight.Web `
  -c Release `
  --no-build
```

## 4. Optional browser flow

```powershell
$env:CS2_STAGE7_BROWSER_E2E = "1"
$env:CS2_WEB_BASE_URL = "http://127.0.0.1:5080"
$env:CS2_STAGE6_MUSIC = "C:\media\track.mp3"
$env:CS2_STAGE6_DEMOS = "C:\demos\one.dem;C:\demos\two.dem"
dotnet test .\tests\Cs2Highlight.Web.Tests -c Release `
  --filter "Category=Stage7BrowserE2E"
```

The Stage 7 browser flow reuses the Stage 6 media inputs because it extends the
same end-to-end generation path.

## 5. Real visual and recovery acceptance

Run at least one generation per style and inspect the final MP4:

1. Clean has no aggressive zoom, shake, RGB split, lens warp or hit-stop.
2. Dynamic remains readable and uses restrained primary/accent combinations.
3. Cinematic uses longer, softer motion and does not obscure the killfeed.
4. Aggressive remains bounded: no black borders, invalid crops, unreadable HUD
   or continuous shake.
5. Hit-stop preserves overall video/audio alignment.
6. Effects land after the Stage 6 time-warp mapping and near the intended kill
   or musical accent.
7. Several highlights do not repeat the same primary effect beyond the variety
   limit.
8. Stop Web during composition, restart it, and confirm the persisted locked
   plan and deterministic seed are reused.
9. Temporarily remove an optional FFmpeg filter from the recorded capability
   set and confirm the plan records a fallback warning instead of failing.
10. Confirm terminal logs show capability scan, planning, each render stage,
    FFmpeg progress and artifact persistence.

Archive `dynamic-effect-plan.json`, `dynamic-effect-result.json`,
`ffmpeg-capabilities.json`, the generation report and the final MP4. Record the
CS2, HLAE and FFmpeg versions with the acceptance result.

Until this checklist passes with real demos on the render machine, the project
must report Stage 7 as implemented and fixture-tested, not as a completed real
HLAE/CS2 E2E.
