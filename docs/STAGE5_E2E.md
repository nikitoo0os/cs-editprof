# Stage 5 real render-machine acceptance

Run this on the Windows machine with Steam, CS2, HLAE, AfxHookSource2, FFmpeg,
ffprobe, Go and an interactive desktop. HLAE-launched CS2 must remain offline
and must not join VAC-protected servers.

## Build and environment

```powershell
Set-Location D:\cs-editprof
git pull
.\scripts\build-demo-parser.ps1
dotnet build .\Cs2Highlight.RenderPoC.sln -c Release
dotnet test .\Cs2Highlight.RenderPoC.sln -c Release --no-build

.\scripts\verify-environment.ps1 `
  -SettingsPath .\src\Cs2Highlight.RenderAgent\bin\Release\net8.0\appsettings.local.json
```

Expected: Go tests/build succeed, .NET reports zero warnings/errors, and every
environment check is `True`.

## Start Web

Ensure `src\Cs2Highlight.Web\appsettings.local.json` points to the built parser,
Render Agent, FFmpeg and ffprobe. Startup applies the incremental
`Stage5HighlightCatalog` migration.

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5080"
dotnet run `
  --project .\src\Cs2Highlight.Web `
  -c Release `
  --no-build
```

Open `http://127.0.0.1:5080`.

## Required flow

1. Upload at least two real `.dem` files.
2. Do not refresh the status page. Confirm progress changes automatically.
3. Select one SteamID.
4. Verify Solo plus at least one Double/Triple candidate in the catalog.
5. Exercise category/demo filters, sorting, recommendations and Top N.
6. Verify round, map, demo, score explanation, weapon icon and swap marker.
7. Select at least two moments, including a swap candidate when available.
8. Select `Dynamic`, continue to checkout and complete the test payment.
9. Wait for the video player to appear without pressing F5.
10. Play and download the final MP4.

## Visual acceptance

- POV belongs to the selected SteamID.
- Demo timeline, console, scoreboard, cursor, HLAE UI and debug overlays are
  absent.
- Crosshair, viewmodel, HP, armor, ammo, normal HUD and killfeed remain.
- At least 2–3 seconds remain after the last kill; round-ending clips do not
  stop abruptly.
- Smooth zoom, headshot flash and vignette are synchronized and remain subtle.
- Transitions begin after post-roll and audio remains usable.

Repeat once with `None` as the no-effect baseline and once with `Clean`.
Restart Web during a run and verify persisted recovery.

## Evidence and honest limits

Keep analysis JSON, immutable plan, effect plans, render results, batch reports,
NetCon logs, FFmpeg logs, compilation result, ffprobe output and final MP4.
Capture screenshots proving the clean Gameplay profile and a screen recording
showing status/video updates without F5.

Automated tests on the development machine do not prove that the installed CS2
build honors every UI command, that actual pixels are clean, or that effects
are visually synchronized with real HLAE output. If the demos contain no swap
candidate, record that limitation. Do not mark Stage 5 complete until the real
checks above pass.
