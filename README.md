# CS2 Highlight Render PoC

Windows CLI proof of concept for validating and orchestrating a single CS2 demo-render job. It creates an isolated workspace, generates version-isolated CFG, supervises external processes without shell argument concatenation, records state transitions, verifies a stable output artifact, and writes a structured result.

Before playback, the agent automatically checks its isolated demo copy for the
CS2 July 2026 legacy entity-message-138 regression. Affected demos are repaired
into a separate `_safe138.dem` workspace file; the source demo is never changed.

This repository does **not** claim a completed real HLAE/CS2 E2E until the
acceptance checklist succeeds repeatedly on the render machine. Execution is
intentionally blocked until the exact command set is manually verified and
`AutomationVerified` is enabled.

## Confirmed HLAE surface

The official AfxHookSource2 documentation confirms:

- CS2 launch through HLAE's CS2 Launcher or Custom Loader;
- isolated config via `USRLOCALCSGO` and `-afxDisableSteamStorage`;
- local-only launch flags including `-insecure`, `+sv_lan 1`, and `-console`;
- `mirv_streams record screen enabled 1`;
- `mirv_streams record fps`, `mirv_streams record name`, `mirv_streams record start`, and `mirv_streams record end`;
- `mirv_cmd addAtTick` for tick-scheduled commands;
- `mirv_fov` for FOV override.

Sources: [AfxHookSource2](https://github.com/advancedfx/advancedfx/wiki/AfxHookSource2), [Source2 commands](https://github.com/advancedfx/advancedfx/wiki/Source2%3ACommands), and [Source2 mirv_streams](https://github.com/advancedfx/advancedfx/wiki/Source2%3Amirv_streams).

The unattended HLAE custom-loader CLI is confirmed from the upstream HLAE source:
`-noConfig -customLoader -autoStart -noGui -hookDllPath ... -programPath ... -cmdLine ... -addEnv ...`.
The launcher always forces `-insecure`, uses an isolated `USRLOCALCSGO`, and injects `AfxHookSource2.dll` only into the CS2 process it creates. Normal CS2 launches through Steam are untouched.

The launcher also enables local NetCon with `-afxFixNetCon`. The agent waits
for actual demo initialization before sending `demo_gototick`, switches to
first-person mode, converts `player.steamId` to the 32-bit AccountID expected by
`spec_lock_to_accountid`, then verifies the resulting
`spec_lock_to_accountid` before recording. `player.name` is informational and
is not used for POV selection. It then
schedules the stop command at `endTick` and
gracefully quits the isolated CS2 process. The application refuses to run when
`AutomationVerified=false`.

## Requirements

- Windows 10/11 interactive desktop session
- .NET 8 SDK/runtime
- Steam and CS2
- current HLAE with AfxHookSource2
- FFmpeg and ffprobe supplied with HLAE
- bundled `cs2-demo-playback-fix` compatibility helper (copied by the build)
- at least 1 GiB free on the working drive (real high-quality renders need much more)

Do not connect an HLAE-launched CS2 instance to VAC-protected servers.

## Build and test

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build
```

## Configure

Copy `examples/appsettings.example.json` to `src/Cs2Highlight.RenderAgent/appsettings.local.json`. Local settings are git-ignored and copied beside the executable during build. Fill in executable paths. `HlaeArguments` contains optional extra CS2 launch flags; required isolation and safety flags are added by the application. Keep `AutomationVerified=false` until the manual checklist in `docs/MANUAL_E2E.md` passes.

Environment variables with prefix `CS2RENDER_` may override configuration. Do not commit machine-local absolute paths.

## Run

```powershell
dotnet run --project src/Cs2Highlight.RenderAgent -- render --job examples/render-job.example.json
```

Published executable:

```text
render-agent.exe render --job render-job.json
```

## Stage 2: Demo analysis and highlight detection

Stage 2 turns real CS2 demo events into a deterministic Stage 1 `RenderJob`:

```text
.dem -> Go demo-parser -> demo-analysis.json
     -> C# rules -> highlights.json -> best-highlight.json -> render-job.json
```

Binary parsing stays in the small Go CLI. Highlight rules, scoring, selection,
window calculation, and the adapter to the existing Render Agent contract stay
in .NET. The analysis command does not start HLAE or CS2.

### Requirements and build

- Go 1.24 or newer (development used Go 1.26.5);
- .NET 8 SDK;
- `demoinfocs-golang/v5` v5.2.0, pinned by `go.mod`.

```powershell
.\scripts\build-demo-parser.ps1
dotnet build .\Cs2Highlight.RenderPoC.sln -c Release
dotnet test .\Cs2Highlight.RenderPoC.sln -c Release --no-build
```

The parser can also be built directly:

```powershell
cd .\tools\demo-parser
go test ./...
go build -trimpath -o .\bin\demo-parser.exe .\cmd\demo-parser
```

### Run

Low-level parser commands:

```powershell
.\artifacts\demo-parser\demo-parser.exe version
.\artifacts\demo-parser\demo-parser.exe validate --input "D:\demos\match.dem"
.\artifacts\demo-parser\demo-parser.exe analyze `
  --input "D:\demos\match.dem" `
  --output "D:\analysis\demo-analysis.json" `
  --pretty `
  --log-file "D:\analysis\logs\demo-parser.log"
```

Complete Stage 2 pipeline:

```powershell
dotnet .\src\Cs2Highlight.Cli\bin\Release\net8.0\cs2-highlight.dll analyze `
  --demo "D:\demos\match.dem" `
  --output "D:\analysis\match-001" `
  --steam-id "76561198000000001" `
  --parser-path ".\artifacts\demo-parser\demo-parser.exe"
```

Pass `--steam-id` to detect and select highlights only for that player. Without
it, the pipeline selects the highest-scoring highlight across the whole match.

The output directory must be empty. Successful analysis creates:

```text
demo-analysis.json
highlights.json
best-highlight.json
render-job.json
logs/demo-parser.log
logs/highlight-detector.log
```

The contracts are versioned as `1.0` and documented under `contracts/`.
Steam IDs are JSON strings. Ticks remain integer server ticks from the demo.
The generated `render-job.json` uses the existing Stage 1 model without renamed
or parallel fields and can be passed directly to:

```powershell
render-agent.exe render --job "D:\analysis\match-001\render-job.json"
```

### Detection and scoring

Kills are grouped by round and stable killer ID. Missing killers, suicides, and
teamkills are excluded. Adjacent kills may be at most six seconds apart and a
sequence may span at most twelve seconds. Only the maximal double/triple/quad/
ace candidate is emitted for a sequence. Render windows use three-second
pre-roll/post-roll and are clamped to round and demo bounds.

Scoring is explainable and serialized as `scoreBreakdown`: kill count, headshot
streak, type, fast-sequence, round-win, and round-ending bonuses. Best selection
uses score, kills, headshots, shorter duration, round, tick, and stable ID in
that order. No random value or current timestamp affects the decision.

### Stage 2 limitations and real verification

- Clutch, cinematic camera, positions, economy, grenade trajectories, editing,
  and ML scoring are outside this stage.
- Headshot streak is currently a tag and score bonus on a multikill, not a
  separate overlapping clip.
- Round-end reasons unavailable from the parser remain `null`.
- Demos without stable SteamID cannot produce a Stage 1 render job.
- The parser spike and complete pipeline were verified on three real fixtures
  from the official `markus-wa/cs-demos-2` regression set:
  - `s2.dem`: `de_ancient`, 144 kills, 10 candidates, TripleKill,
    SteamID64 `76561198213282160`, ticks `28123..28837`;
  - `1_2v2_6thAug23_64cf951f9b4ce6b86c73b089.dem`: `de_overpass`,
    38 kills, 4 candidates, DoubleKill, SteamID64 `76561197986329856`,
    ticks `45947..46445`;
  - `Anubis_ShortMatch_2023-08-04.dem`: `de_anubis`, 99 kills,
    10 candidates, TripleKill, SteamID64 `76561198071076641`,
    ticks `47704..48523`.
- These regression fixtures are from 2023. Current user demos still need
  installed-machine verification before declaring broad format compatibility.

For parser failures, inspect structured stderr and `logs/demo-parser.log`.
`MALFORMED_DEMO` is distinct from the valid `NO_HIGHLIGHTS_FOUND` result.
Local real demos belong under `tools/demo-parser/testdata/local/` and must not
be committed.

If `proxy.golang.org` fails because a network filter only supports obsolete TLS,
`build-demo-parser.ps1` automatically retries module downloads directly through
Git. The dependencies remain pinned by `go.mod` and verified against `go.sum`.
The fallback can also be requested explicitly:

```powershell
.\scripts\build-demo-parser.ps1 -Direct
```

After one successful job, run consecutive acceptance jobs without rebooting:

```powershell
.\scripts\run-acceptance.ps1 -JobPath .\render-job.json -Count 3
```

The script creates unique job IDs and output directories and stops at the first
failure. Per-run inputs and result JSON files are saved under
`artifacts\acceptance-runs`.

## Job and output

See `examples/render-job.example.json`. Existing non-empty output directories are rejected to prevent silent overwrites. Per-job workspaces contain:

```text
input/  config/  raw/  output/  logs/  state/
```

`state/render-state.json` contains the latest transition, `render-state.jsonl`
contains history, and `render-result.json` contains the final structured
result. On success, an ffprobe-validated MP4 is copied as
`raw-highlight.mp4` to the requested output directory.

`logs/demo-compatibility-repair.log` reports either `REPAIRED` with removal
statistics or `CLEAN`. The bundled helper is Apache-2.0 licensed; provenance,
license, and notices are stored in `third_party/cs2-demo-playback-fix`.
`logs/netcon.log` contains received CS2 console lines and every command sent by
the agent, prefixed with `>`.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | success |
| 2 | invalid CLI arguments |
| 10 | invalid render job |
| 20 | environment invalid, renderer busy, or automation unconfirmed |
| 30 | HLAE launch failed |
| 31 | CS2 launch timeout |
| 32 | CS2 exited unexpectedly |
| 40 | demo compatibility repair or control failed |
| 50 | recording failed |
| 60 | output verification failed |
| 70 | cancelled |
| 99 | unexpected error |

## Cancellation and recovery

Ctrl+C cancels waits and kills only the process tree started by the current
job. A named cross-process semaphore prevents concurrent render jobs and is
safe across asynchronous continuations. Diagnostic workspace data is preserved
on failure.

Use `scripts/kill-render-processes.ps1 -StateDirectory <job-state-path> -WhatIf` before any manual cleanup. The script only acts on PIDs recorded for the job and prompts by default; it never kills processes by name.

## Troubleshooting

- Run `scripts/verify-environment.ps1 -SettingsPath <settings.json>`.
- Inspect `logs/netcon.log`, `logs/hlae.stdout.log`, `logs/hlae.stderr.log`, and `state/render-state.jsonl`.
- If automation is rejected, follow `docs/MANUAL_E2E.md`; do not bypass the guard without testing the installed builds.
- If output is missing, use HLAE console help for `mirv_streams` and verify both FFmpeg and ffprobe.
- If CS2 reports `Unknown message type 138` or `Failed to parse message`, inspect
  `logs/demo-compatibility-repair.log` and confirm the generated CFG uses the
  `_safe138.dem` workspace copy.
- See `docs/KNOWN_ISSUES.md` for current research gaps.

## Known working environment

- Windows version: Windows 11 build 26200 (build/test only)
- .NET version: SDK 9.0.316 targeting .NET 8
- Steam version: not verified
- CS2 build: not verified
- HLAE version: not installed / not verified
- GPU and driver: not verified
- Tested demo version: not available
