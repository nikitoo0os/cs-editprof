# Stage 6 real music-driven E2E

Automated tests do not prove clean CS2 pixels, musical alignment, browser
playback or acceptable sound. Run this checklist on the Windows render machine.

1. Install Python 3.11 and build `demo-parser`, `music-analyzer` and the
   Release solution. Use
   `.\scripts\build-music-analyzer.ps1 -PythonVersion 3.11`; do not package it
   with Python 3.12+.
2. Verify HLAE/CS2/FFmpeg and use two real demos.
3. Select one SteamID, at least one Solo and one multikill.
4. Upload a rights-confirmed MP3/WAV/FLAC with a clearly audible accent.
5. Choose Dynamic, Expressive and one color preset; complete test payment.
6. Preserve `music-analysis.json`, `music-edit-plan.json`, batch state,
   FFmpeg logs, final probe data and the generation report.
7. Review every clip for loading screen, demo controls, lower overlay,
   console, scoreboard, correct POV, complete SafeEnd and readable HUD.
8. Review the final MP4 for music/gameplay balance, clipping, color
   consistency and kill/anchor alignment.
9. Refresh/reconnect during processing, restart once, and confirm successful
   clips and payment are not repeated.

Run the opt-in browser flow from a second PowerShell window:

```powershell
$env:CS2_STAGE6_BROWSER_E2E = "1"
$env:CS2_WEB_BASE_URL = "http://127.0.0.1:5080"
$env:CS2_STAGE6_DEMOS = "D:\fixtures\one.dem;D:\fixtures\two.dem"
$env:CS2_STAGE6_MUSIC = "D:\fixtures\music.wav"
$env:CS2_STAGE6_STEAM_ID = "76561199031052443"
dotnet test .\tests\Cs2Highlight.Web.Tests -c Release `
  --filter "Category=Stage6BrowserE2E"
```

After completion, validate machine-readable artifacts:

```powershell
.\scripts\verify-stage6-output.ps1 `
  -GenerationRoot "D:\Cs2Highlights\web\generations\<publicId>" `
  -FfprobePath "D:\Tools\ffmpeg\bin\ffprobe.exe"
```

Both commands are intentionally opt-in because the scenario starts the real
renderer and can take many minutes.

The current local development machine has no HLAE/CS2 installation and could
not package librosa because its Python 3.8 TLS proxy is broken. Therefore real
Stage 6 E2E remains pending until evidence from the render machine is attached.
