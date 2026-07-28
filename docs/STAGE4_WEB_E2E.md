# Stage 4 Web acceptance

Stage 4 is accepted only after both the automated suite and the real
Web → HLAE/CS2 → FFmpeg scenario below pass. Fake-process tests do not replace
the real scenario.

## Configure the render machine

Run from the repository root:

```powershell
Copy-Item .\examples\appsettings.web.example.json `
  .\src\Cs2Highlight.Web\appsettings.local.json
notepad .\src\Cs2Highlight.Web\appsettings.local.json

.\scripts\build-demo-parser.ps1
dotnet build .\Cs2Highlight.RenderPoC.sln -c Release
dotnet test .\Cs2Highlight.RenderPoC.sln -c Release --no-build

.\scripts\verify-environment.ps1 `
  -SettingsPath .\src\Cs2Highlight.RenderAgent\bin\Release\net8.0\appsettings.local.json
```

Every environment check must be `True`. The Web configuration must use the
same installed demo parser, render agent, FFmpeg and FFprobe paths. Do not run
ordinary CS2 or HLAE while the worker owns the renderer.

Start the site in the interactive Windows desktop session:

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5080"
dotnet run --project .\src\Cs2Highlight.Web -c Release --no-build
```

Open `http://127.0.0.1:5080`. `/health/live` must be healthy and
`/health/ready` must report SQLite, storage, free disk space, parser, render
agent, FFmpeg and FFprobe as available.

## Real multi-demo acceptance

1. Upload at least two real `.dem` files. Upload the same file twice once and
   verify that the duplicate is skipped by SHA-256.
2. Wait for both analyses and select a SteamID present in at least one demo.
3. Select Top N = 2 or greater and a deterministic output order.
4. At checkout verify `$1.00 USD`, then complete the test payment once.
5. Do not enter commands in CS2. Wait for sequential Stage 3 renders and the
   FFmpeg composition.
6. Verify that the result page exposes one player only, not intermediate clips.
7. Play the video, seek to a later position, and download it.
8. Inspect `output/generation-report.json`,
   `output/compilation-result.json`, and `plan/generation-plan.json`.
9. Run FFprobe independently:

```powershell
& "D:\Tools\ffmpeg\bin\ffprobe.exe" `
  -v error -show_streams -show_format `
  "D:\Cs2Highlights\web\generations\<public-id>\output\final-highlights.mp4"
```

The MP4 must be non-empty, H.264/yuv420p with AAC audio, have the configured
dimensions/FPS, and contain every successfully rendered selected clip.

## HTTP and persistence checks

Replace `<public-id>` with the opaque ID from the browser URL:

```powershell
curl.exe -I -H "Range: bytes=0-1023" `
  "http://127.0.0.1:5080/generations/<public-id>/video"
curl.exe -OJ `
  "http://127.0.0.1:5080/generations/<public-id>/video?download=true"
```

The range request must return `206 Partial Content`. Close and reopen the
browser URL and verify that the completed order remains available.

## Restart acceptance

Start another paid generation, stop the Web process during Stage 3, and start
it again with the same database/storage configuration. Verify that completed
batch items are reused, compilation restarts safely from its temporary file,
payment is not repeated, and exactly one final MP4 is published.

Save the Web log, render-agent logs, generation report, compilation result and
FFprobe output under a separate acceptance evidence directory. Never commit
real demos, videos, secrets, or machine-local settings.

The current MVP normalizes and concatenates clips with a cut. The persisted
`Fade` setting is reserved for a later FFmpeg `xfade` implementation; it does
not yet change the transition.
