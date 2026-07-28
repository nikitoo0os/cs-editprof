# Stage 6 real music-driven E2E

Automated tests do not prove clean CS2 pixels, musical alignment, browser
playback or acceptable sound. Run this checklist on the Windows render machine.

1. Build `demo-parser`, `music-analyzer` and the Release solution.
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

The current local development machine has no HLAE/CS2 installation and could
not package librosa because its Python 3.8 TLS proxy is broken. Therefore real
Stage 6 E2E remains pending until evidence from the render machine is attached.
