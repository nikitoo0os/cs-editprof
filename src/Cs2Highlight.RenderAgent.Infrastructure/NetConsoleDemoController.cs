using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class NetConsoleDemoController(
    RenderEnvironmentOptions options,
    IStateJournal stateJournal) : IDemoController
{
    private static readonly Regex SeekFinishedTickPattern = new(
        @"Demo Skipping (?:finished at tick\s+|flushing last .*?, tick\s+)(?<tick>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex ActiveGameLoopPattern = new(
        @"@\s*Current\s*:\s*game\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private const string NetConReadyMarker = "AFX_RENDER_NETCON_READY";
    private const string DemoStatusEndMarker = "AFX_RENDER_DEMO_STATUS_END";
    private const string SeekFinishedMarker = "Demo Skipping finished at tick";
    private const string SeekFlushedMarker = "Demo Skipping flushing last";
    private const string StartReadyMarker = "AFX_RENDER_START_READY";
    private const string SafeTailMarker = "AFX_RENDER_SAFE_TAIL";
    private const string RecordingEndMarker = "AFX_RENDER_RECORDING_END";
    private const string PresentationVerificationEndMarker =
        "AFX_RENDER_PRESENTATION_VERIFY_END";
    private const long MaximumCampathTickDrift = 2;
    private static readonly JsonSerializerOptions ReportJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record HlaeCameraCommandProbe(
        string Command,
        bool Supported,
        IReadOnlyList<string> Output);
    private sealed record HlaeCameraCommandReport(
        DateTimeOffset ProbedAt,
        string HlaeVersion,
        IReadOnlyList<HlaeCameraCommandProbe> Commands);
    private sealed record AppliedCameraKeyframe(
        long Tick,
        RenderVector3 Position,
        RenderVector3 Rotation,
        double Fov);
    private sealed record AppliedCameraReport(
        RenderCameraMode Mode,
        string MapName,
        string VerificationId,
        bool CalibrationSpike,
        bool ManualSpikeVerified,
        bool ReturnedToWarmupAfterKeyframeBuild,
        bool CampathEnabled,
        IReadOnlyList<AppliedCameraKeyframe> Keyframes,
        IReadOnlyList<string> CampathOutput);
    private sealed record ParsedCampathKeyframe(
        long Tick,
        RenderVector3 Position,
        RenderVector3 Rotation,
        double Fov);

    public async Task ControlAsync(
        RenderJob job,
        RenderWorkspace workspace,
        DemoLoadMode loadMode,
        CancellationToken cancellationToken)
    {
        await using NetConsoleConnection connection = await ConnectAsync(
            Path.Combine(workspace.Logs, "netcon.log"),
            TimeSpan.FromSeconds(options.ProcessStartupTimeoutSeconds),
            cancellationToken);
        ICaptureUiController captureUi = new NetConCaptureUiController(connection);

        await stateJournal.WriteAsync(
            workspace,
            RenderState.LoadingDemo,
            "Connected to CS2 NetCon; performing active readiness handshake.",
            cancellationToken);
        await WaitForNetConReadyAsync(
            connection,
            TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
            cancellationToken);
        await ConfigureRecordingAsync(connection, job, workspace, cancellationToken);
        await captureUi.ApplyAsync(job.EffectivePresentationMode, cancellationToken);
        if (loadMode == DemoLoadMode.Start)
        {
            await stateJournal.WriteAsync(
                workspace,
                RenderState.LoadingDemo,
                "CS2 console is ready; starting the demo and waiting for Connected [DEMO].",
                cancellationToken);
            await connection.SendAsync(
                $"playdemo \"{Source2ScriptGenerator.EscapeCfg(workspace.PreparedDemoPath)}\"",
                cancellationToken);
            await WaitForDemoReadyAsync(
                connection,
                TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
                cancellationToken);
            await Task.Delay(
                TimeSpan.FromSeconds(options.DemoInitializationStabilizationSeconds),
                cancellationToken);
            // CS2 opens the demo playback panel when a demo is initially
            // loaded. `demoui` is a toggle rather than a cvar, so close it
            // exactly once for a new playback session and never while reusing
            // that session for subsequent clips.
            await connection.SendAsync("demoui", cancellationToken);
        }
        else
        {
            await stateJournal.WriteAsync(
                workspace,
                RenderState.LoadingDemo,
                "Reusing the current demo without playdemo/map reload; validating active playback before seeking.",
                cancellationToken);
            await WaitForDemoReadyAsync(
                connection,
                TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
                cancellationToken);
        }

        // Loading or reusing a demo can recreate Panorama/demo controls. Apply
        // the clean presentation state again before any camera preview work.
        await captureUi.ApplyAsync(
            job.EffectivePresentationMode,
            cancellationToken);

        HlaeCameraCommandReport cameraCommandReport =
            await ProbeCameraCommandsAsync(connection, cancellationToken);
        await PersistCameraCommandReportAsync(
            cameraCommandReport,
            workspace,
            job.OutputDirectory,
            cancellationToken);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.WaitingForCs2,
            $"HLAE camera commands probed: " +
            $"{cameraCommandReport.Commands.Count(value => value.Supported)}/" +
            $"{cameraCommandReport.Commands.Count} supported " +
            $"(version {cameraCommandReport.HlaeVersion}).",
            cancellationToken);

        long warmupTick = ComputeWarmupTick(job.Segment, options.Warmup);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.SeekingToWarmup,
            loadMode == DemoLoadMode.Start
                ? $"Demo initialized; seeking to warmup tick {warmupTick}."
                : $"Current demo is still active; seeking directly to warmup tick {warmupTick}.",
            cancellationToken);
        await SeekToWarmupAsync(
            connection,
            warmupTick,
            TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
            cancellationToken);
        await connection.SendAsync("demo_pause", cancellationToken);
        await captureUi.ApplyAsync(job.EffectivePresentationMode, cancellationToken);

        ulong steamId64 = GetSteamId64(job.Player);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.SelectingPlayer,
            $"Seek completed; locking POV to SteamID64 {steamId64}.",
            cancellationToken);
        await connection.SendAsync("mirv_cvar_unhide_all", cancellationToken);
        await connection.SendAsync("spec_mode 1", cancellationToken);
        uint accountId = GetAccountId(steamId64);
        await connection.SendAsync(
            $"spec_lock_to_accountid {accountId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        await VerifySelectedPlayerAsync(connection, steamId64, cancellationToken);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.ApplyingCameraPlan,
            $"Applying {job.Camera.Mode} camera plan with " +
            $"{job.Camera.Keyframes.Count} keyframe(s).",
            cancellationToken);
        await ApplyCameraPlanAsync(
            connection,
            job,
            workspace,
            warmupTick,
            cancellationToken);
        await captureUi.ApplyAsync(job.EffectivePresentationMode, cancellationToken);

        if (warmupTick < job.Segment.StartTick)
        {
            await stateJournal.WriteAsync(
                workspace,
                RenderState.WarmingUp,
                $"Advancing demo from warmup tick {warmupTick} to start tick {job.Segment.StartTick}.",
                cancellationToken);
            await connection.SendAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"mirv_cmd addAtTick {job.Segment.StartTick} \"demo_pause; echo {StartReadyMarker}\""),
                cancellationToken);
            await connection.SendAsync("demo_resume", cancellationToken);
            await stateJournal.WriteAsync(
                workspace,
                RenderState.WaitingForGameplayReady,
                "Waiting for advancing demo playback, stable POV and the actual start tick.",
                cancellationToken);
            await connection.WaitForAsync(
                StartReadyMarker,
                TimeSpan.FromSeconds(options.Warmup.MaximumGameplayReadyWaitSeconds),
                cancellationToken);
            // mirv_cmd keeps scheduled commands until they are explicitly removed.
            // If the start-tick pause remains registered, demo_resume at the same
            // tick immediately executes the pause again and recording never advances.
            await connection.SendAsync("mirv_cmd clear", cancellationToken);
        }

        await stateJournal.WriteAsync(
            workspace,
            RenderState.AdvancingToStartTick,
            $"Actual recording start tick {job.Segment.StartTick} reached while recording is stopped.",
            cancellationToken);
        await connection.SendAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mirv_cmd addAtTick {job.Segment.EndTick} \"mirv_streams record end; demo_pause; echo {RecordingEndMarker}\""),
            cancellationToken);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.ApplyingCaptureProfile,
            $"Applying {job.EffectivePresentationMode} presentation mode ({CaptureUiProfileAdapter.TemplateVersion}).",
            cancellationToken);
        if (options.Warmup.ReapplyCaptureProfileAfterWarmup)
            await captureUi.ApplyAsync(job.EffectivePresentationMode, cancellationToken);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.VerifyingCaptureProfile,
            "Verifying presentation cvars before recording.",
            cancellationToken);
        PresentationStateReport presentationReport =
            await VerifyPresentationStateAsync(
                connection,
                job,
                cancellationToken);
        await PersistPresentationReportAsync(
            presentationReport,
            workspace,
            job.OutputDirectory,
            cancellationToken);
        if (job.ContainsFirstPersonWeaponFire &&
            !presentationReport.State.WeaponStateValid)
        {
            throw new InvalidOperationException(
                "WEAPON_HIDDEN_DURING_POV_COMBAT: r_drawviewmodel was not " +
                "confirmed enabled before first-person combat recording.");
        }
        await stateJournal.WriteAsync(
            workspace,
            RenderState.StabilizingCaptureProfile,
            "Capture profile commands applied; stabilizing before recording.",
            cancellationToken);
        const string captureMarker = "AFX_RENDER_CAPTURE_PROFILE_APPLIED";
        await connection.SendAsync($"echo {captureMarker}", cancellationToken);
        await connection.WaitForAsync(
            captureMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        await Task.Delay(
            TimeSpan.FromSeconds(options.Warmup.MinimumWallClockStabilizationSeconds),
            cancellationToken);
        bool hasSafeTailMarker = job.Segment.LastKillTick is long lastKill &&
            lastKill >= job.Segment.StartTick &&
            lastKill < job.Segment.EndTick;
        if (hasSafeTailMarker)
        {
            await connection.SendAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"mirv_cmd addAtTick {job.Segment.LastKillTick} \"echo {SafeTailMarker}\""),
                cancellationToken);
        }
        await stateJournal.WriteAsync(
            workspace,
            RenderState.Recording,
            $"Starting recording through tick {job.Segment.EndTick}.",
            cancellationToken);
        await connection.SendAsync("mirv_streams record start", cancellationToken);
        await connection.SendAsync("echo AFX_RENDER_RECORDING_START", cancellationToken);
        await connection.SendAsync("demo_resume", cancellationToken);

        IReadOnlyList<string> recordingOutput = await connection.ReadThroughAsync(
            RecordingEndMarker,
            TimeSpan.FromSeconds(job.TimeoutSeconds),
            cancellationToken);
        if (hasSafeTailMarker &&
            recordingOutput.Any(line =>
                line.Contains(SafeTailMarker, StringComparison.Ordinal)))
        {
            await stateJournal.WriteAsync(
                workspace,
                RenderState.RecordingSafeTail,
                $"Last kill reached; preserving recording tail through tick {job.Segment.EndTick}.",
                cancellationToken);
        }
        await stateJournal.WriteAsync(
            workspace,
            RenderState.StoppingRecording,
            $"Recording stopped at tick {job.Segment.EndTick}.",
            cancellationToken);
    }

    public static long ComputeWarmupTick(
        RenderSegment segment,
        RenderWarmupOptions warmup)
    {
        if (segment.TickRate is not int tickRate || tickRate <= 0)
            return segment.StartTick;
        long warmupTicks = (long)Math.Round(
            Math.Max(0, warmup.WarmupGameSeconds) * tickRate,
            MidpointRounding.AwayFromZero);
        long lowerBound = Math.Max(0, segment.RoundStartTick ?? 0);
        return Math.Max(lowerBound, segment.StartTick - warmupTicks);
    }

    private static async Task ConfigureRecordingAsync(
        NetConsoleConnection connection,
        RenderJob job,
        RenderWorkspace workspace,
        CancellationToken cancellationToken)
    {
        // A reused CS2 session still contains the previous clip's addAtTick
        // commands and output path. Reset both before seeking to the next clip.
        await connection.SendAsync("mirv_cmd clear", cancellationToken);
        await connection.SendAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mirv_fov {job.Video.Fov}"),
            cancellationToken);
        await connection.SendAsync("mirv_streams record screen enabled 1", cancellationToken);
        await connection.SendAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mirv_streams record fps {job.Video.Fps}"),
            cancellationToken);
        await connection.SendAsync(
            $"mirv_streams record name \"{Source2ScriptGenerator.EscapeCfg(workspace.Raw)}\"",
            cancellationToken);
    }

    private async Task ApplyCameraPlanAsync(
        NetConsoleConnection connection,
        RenderJob job,
        RenderWorkspace workspace,
        long warmupTick,
        CancellationToken cancellationToken)
    {
        await connection.SendAsync("mirv_campath enabled 0", cancellationToken);
        await connection.SendAsync("mirv_campath clear", cancellationToken);
        await connection.SendAsync("mirv_input end", cancellationToken);
        if (job.Camera.Mode == RenderCameraMode.PlayerPov)
        {
            await connection.SendAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"mirv_fov {job.Video.Fov}"),
                cancellationToken);
            await PersistAppliedCameraReportAsync(
                new AppliedCameraReport(
                    RenderCameraMode.PlayerPov,
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    false,
                    false,
                    [],
                    []),
                workspace,
                job.OutputDirectory,
                cancellationToken);
            return;
        }

        string installedHlaeVersion = File.Exists(options.HlaeExecutablePath)
            ? FileVersionInfo.GetVersionInfo(options.HlaeExecutablePath)
                .ProductVersion ?? string.Empty
            : "unknown";
        if (!installedHlaeVersion.StartsWith(
                job.Camera.HlaeVersionPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"CAMERA_HLAE_VERSION_MISMATCH: required " +
                $"{job.Camera.HlaeVersionPrefix}, installed " +
                $"{installedHlaeVersion}.");
        }
        await connection.SendAsync("mirv_fov default", cancellationToken);
        await connection.SendAsync("mirv_input camera", cancellationToken);
        List<AppliedCameraKeyframe> applied = [];
        List<string> campathOutput = [];
        bool returnedToWarmup = false;
        bool campathEnabled = false;
        if (job.Camera.Mode == RenderCameraMode.Static)
        {
            RenderCameraKeyframe keyframe = job.Camera.Keyframes.Single();
            await ApplyCameraTransformAsync(
                connection,
                keyframe,
                cancellationToken);
            applied.Add(ToApplied(keyframe));
        }
        else
        {
            for (int index = 0; index < job.Camera.Keyframes.Count; index++)
            {
                RenderCameraKeyframe keyframe = job.Camera.Keyframes[index];
                await SeekToWarmupAsync(
                    connection,
                    keyframe.Tick,
                    TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
                    cancellationToken);
                await connection.SendAsync("demo_pause", cancellationToken);
                await ApplyCameraTransformAsync(
                    connection,
                    keyframe,
                    cancellationToken);
                await VerifyCameraTransformAsync(
                    connection,
                    keyframe,
                    cancellationToken);
                string addMarker =
                    $"AFX_RENDER_CAMPATH_ADD_{index.ToString(CultureInfo.InvariantCulture)}";
                await connection.SendAsync("mirv_campath add", cancellationToken);
                await connection.SendAsync($"echo {addMarker}", cancellationToken);
                await connection.WaitForAsync(
                    addMarker,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                applied.Add(ToApplied(keyframe));
            }

            const string printMarker = "AFX_RENDER_CAMPATH_PRINT_END";
            await connection.SendAsync("mirv_campath print", cancellationToken);
            await connection.SendAsync($"echo {printMarker}", cancellationToken);
            campathOutput.AddRange(await connection.ReadThroughAsync(
                printMarker,
                TimeSpan.FromSeconds(5),
                cancellationToken));
            VerifyCampathOutput(
                campathOutput,
                job.Camera.Keyframes);
            await SeekToWarmupAsync(
                connection,
                warmupTick,
                TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
                cancellationToken);
            returnedToWarmup = true;
            await connection.SendAsync("demo_pause", cancellationToken);
            await connection.SendAsync("mirv_input end", cancellationToken);
            await connection.SendAsync("mirv_campath enabled 1", cancellationToken);
            campathEnabled = true;
        }

        AppliedCameraReport report = new(
            job.Camera.Mode,
            job.Camera.MapName,
            job.Camera.VerificationId,
            job.Camera.CalibrationSpike,
            job.Camera.ManualSpikeVerified,
            returnedToWarmup,
            campathEnabled,
            applied,
            campathOutput);
        await PersistAppliedCameraReportAsync(
            report,
            workspace,
            job.OutputDirectory,
            cancellationToken);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.VerifyingCameraPlan,
            job.Camera.Mode == RenderCameraMode.Campath
                ? $"Campath built with {applied.Count} keyframes and seeked back to warmup tick {warmupTick}."
                : "Static free-camera transform applied.",
            cancellationToken);
    }

    private static async Task ApplyCameraTransformAsync(
        NetConsoleConnection connection,
        RenderCameraKeyframe keyframe,
        CancellationToken cancellationToken)
    {
        await connection.SendAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mirv_input position {keyframe.Position.X} {keyframe.Position.Y} {keyframe.Position.Z}"),
            cancellationToken);
        await connection.SendAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mirv_input angles {keyframe.Rotation.X} {keyframe.Rotation.Y} {keyframe.Rotation.Z}"),
            cancellationToken);
        await connection.SendAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mirv_input fov {keyframe.Fov}"),
            cancellationToken);
    }

    private static async Task VerifyCameraTransformAsync(
        NetConsoleConnection connection,
        RenderCameraKeyframe expected,
        CancellationToken cancellationToken)
    {
        string settleMarker =
            $"AFX_RENDER_CAMERA_TRANSFORM_{expected.Tick.ToString(CultureInfo.InvariantCulture)}";
        await connection.SendAsync($"echo {settleMarker}", cancellationToken);
        await connection.WaitForAsync(
            settleMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        await Task.Delay(100, cancellationToken);

        await connection.SendAsync("mirv_input position", cancellationToken);
        await connection.SendAsync("mirv_input angles", cancellationToken);
        await connection.SendAsync("mirv_input fov", cancellationToken);
        string verifyMarker = $"{settleMarker}_VERIFY";
        await connection.SendAsync($"echo {verifyMarker}", cancellationToken);
        IReadOnlyList<string> output = await connection.ReadThroughAsync(
            verifyMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        List<double[]> vectors = output
            .Select(ParseCurrentVector)
            .Where(value => value is not null)
            .Cast<double[]>()
            .ToList();
        double? fov = output
            .Select(ParseCurrentScalar)
            .LastOrDefault(value => value.HasValue);
        if (vectors.Count < 2 ||
            !Approximately(vectors[0], expected.Position) ||
            !Approximately(vectors[1], expected.Rotation) ||
            fov is null ||
            Math.Abs(fov.Value - expected.Fov) > 0.02)
        {
            throw new InvalidOperationException(
                $"CAMERA_TRANSFORM_MISMATCH at tick {expected.Tick}: " +
                $"expected position={expected.Position}, " +
                $"rotation={expected.Rotation}, fov={expected.Fov}; " +
                $"HLAE output={string.Join(" | ", output)}");
        }
        await Task.Delay(50, cancellationToken);
    }

    private static double[]? ParseCurrentVector(string line)
    {
        Match match = Regex.Match(
            line,
            @"Current value:\s*(?<x>[-+]?\d+(?:\.\d+)?)\s+" +
            @"(?<y>[-+]?\d+(?:\.\d+)?)\s+" +
            @"(?<z>[-+]?\d+(?:\.\d+)?)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;
        return
        [
            double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture)
        ];
    }

    private static double? ParseCurrentScalar(string line)
    {
        Match match = Regex.Match(
            line,
            @"Current value:\s*(?<value>[-+]?\d+(?:\.\d+)?)\s*$",
            RegexOptions.CultureInvariant);
        return match.Success
            ? double.Parse(
                match.Groups["value"].Value,
                CultureInfo.InvariantCulture)
            : null;
    }

    private static bool Approximately(
        double[] actual,
        RenderVector3 expected) =>
        Math.Abs(actual[0] - expected.X) <= 0.02 &&
        Math.Abs(actual[1] - expected.Y) <= 0.02 &&
        Math.Abs(actual[2] - expected.Z) <= 0.02;

    private static void VerifyCampathOutput(
        IReadOnlyList<string> output,
        IReadOnlyList<RenderCameraKeyframe> expected)
    {
        ParsedCampathKeyframe[] actual = output
            .Select(ParseCampathKeyframe)
            .Where(value => value is not null)
            .Cast<ParsedCampathKeyframe>()
            .ToArray();
        if (actual.Length != expected.Count)
        {
            throw new InvalidOperationException(
                $"CAMERA_CAMPATH_KEYFRAME_COUNT_MISMATCH: " +
                $"expected={expected.Count}, actual={actual.Length}.");
        }
        for (int index = 0; index < expected.Count; index++)
        {
            RenderCameraKeyframe expectedValue = expected[index];
            ParsedCampathKeyframe actualValue = actual[index];
            if (Math.Abs(actualValue.Tick - expectedValue.Tick) >
                    MaximumCampathTickDrift ||
                !Approximately(
                    [
                        actualValue.Position.X,
                        actualValue.Position.Y,
                        actualValue.Position.Z
                    ],
                    expectedValue.Position) ||
                !Approximately(
                    [
                        actualValue.Rotation.X,
                        actualValue.Rotation.Y,
                        actualValue.Rotation.Z
                    ],
                    expectedValue.Rotation) ||
                Math.Abs(actualValue.Fov - expectedValue.Fov) > 0.02)
            {
                throw new InvalidOperationException(
                    $"CAMERA_CAMPATH_KEYFRAME_MISMATCH at index {index}: " +
                    $"expected={expectedValue}; actual={actualValue}; " +
                    $"maximumTickDrift={MaximumCampathTickDrift}.");
            }
        }
    }

    private static ParsedCampathKeyframe? ParseCampathKeyframe(string line)
    {
        const string number = @"[-+]?\d+(?:\.\d+)?";
        Match match = Regex.Match(
            line,
            $@"^\s*[YN]\s+[yn]\s+\d+\s*:\s*(?<tick>\d+).*?-\>\s*" +
            $@"\(\s*(?<x>{number})\s+(?<y>{number})\s+(?<z>{number})\s*\)\s*" +
            $@"(?<fov>{number})\s*\(\s*(?<pitch>{number})\s+" +
            $@"(?<yaw>{number})\s+(?<roll>{number})\s*\)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;
        return new ParsedCampathKeyframe(
            long.Parse(
                match.Groups["tick"].Value,
                CultureInfo.InvariantCulture),
            new RenderVector3(
                ParseDouble(match, "x"),
                ParseDouble(match, "y"),
                ParseDouble(match, "z")),
            new RenderVector3(
                ParseDouble(match, "pitch"),
                ParseDouble(match, "yaw"),
                ParseDouble(match, "roll")),
            ParseDouble(match, "fov"));
    }

    private static double ParseDouble(Match match, string group) =>
        double.Parse(
            match.Groups[group].Value,
            CultureInfo.InvariantCulture);

    private static AppliedCameraKeyframe ToApplied(
        RenderCameraKeyframe keyframe) =>
        new(
            keyframe.Tick,
            keyframe.Position,
            keyframe.Rotation,
            keyframe.Fov);

    private static async Task PersistAppliedCameraReportAsync(
        AppliedCameraReport report,
        RenderWorkspace workspace,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(report, ReportJsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.State, "applied-camera-report.json"),
            json,
            cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "applied-camera-report.json"),
            json,
            cancellationToken);
    }

    private static async Task<PresentationStateReport> VerifyPresentationStateAsync(
        NetConsoleConnection connection,
        RenderJob job,
        CancellationToken cancellationToken)
    {
        string[] names =
        [
            "cl_showdemooverlay",
            "cl_drawhud",
            "spec_show_xray",
            "r_drawviewmodel",
            "r_show_build_info",
            "cl_trueview_show_status"
        ];
        foreach (string name in names)
            await connection.SendAsync(name, cancellationToken);
        await connection.SendAsync(
            $"echo {PresentationVerificationEndMarker}",
            cancellationToken);
        IReadOnlyList<string> output = await connection.ReadThroughAsync(
            PresentationVerificationEndMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        Dictionary<string, bool> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            Match match = Regex.Match(
                string.Join('\n', output),
                $"[\"']?{Regex.Escape(name)}[\"']?\\s*=\\s*[\"']?(?<value>true|false|0|1)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                string value = match.Groups["value"].Value;
                values[name] =
                    value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    value == "1";
            }
        }

        bool pov = job.EffectivePresentationMode == CapturePresentationMode.PovCombat;
        bool timelineHidden =
            values.TryGetValue("cl_showdemooverlay", out bool overlay) && !overlay;
        bool spectatorHidden =
            values.TryGetValue("spec_show_xray", out bool xray) && !xray;
        bool hudValid =
            values.TryGetValue("cl_drawhud", out bool hud) && hud == pov;
        bool weaponValid =
            values.TryGetValue("r_drawviewmodel", out bool weapon) && weapon == pov;
        bool debugUiHidden =
            values.TryGetValue("r_show_build_info", out bool buildInfo) &&
            !buildInfo &&
            values.TryGetValue("cl_trueview_show_status", out bool trueViewStatus) &&
            !trueViewStatus;
        bool commandStateVerified =
            timelineHidden && spectatorHidden && hudValid && weaponValid &&
            debugUiHidden;

        List<string> issues = [];
        if (!commandStateVerified)
            issues.Add("PRESENTATION_CVAR_STATE_MISMATCH");
        issues.Add("PRESENTATION_PIXEL_VERIFICATION_PENDING");
        return new PresentationStateReport(
            job.EffectivePresentationMode,
            new PresentationStateVerification(
                timelineHidden,
                DemoControlsHidden: false,
                spectatorHidden,
                debugUiHidden,
                MouseCursorHidden: false,
                weaponValid),
            commandStateVerified,
            PixelStateVerified: false,
            issues);
    }

    private static async Task PersistPresentationReportAsync(
        PresentationStateReport report,
        RenderWorkspace workspace,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(report, ReportJsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.State, "presentation-state-report.json"),
            json,
            cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "presentation-state-report.json"),
            json,
            cancellationToken);
    }

    private async Task<HlaeCameraCommandReport> ProbeCameraCommandsAsync(
        NetConsoleConnection connection,
        CancellationToken cancellationToken)
    {
        string[] commandNames =
        [
            "mirv_campath",
            "mirv_camio",
            "mirv_input",
            "mirv_input position",
            "mirv_input angles",
            "mirv_input fov",
            "mirv_fov",
            "mirv_cmd",
            "mirv_streams"
        ];
        List<HlaeCameraCommandProbe> probes = [];
        for (int index = 0; index < commandNames.Length; index++)
        {
            string command = commandNames[index];
            string endMarker =
                $"AFX_RENDER_CAMERA_PROBE_{index.ToString(CultureInfo.InvariantCulture)}_END";
            await connection.SendAsync(command, cancellationToken);
            await connection.SendAsync($"echo {endMarker}", cancellationToken);
            IReadOnlyList<string> output = await connection.ReadThroughAsync(
                endMarker,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            string[] relevant = output
                .Where(line =>
                    !line.Contains(endMarker, StringComparison.Ordinal))
                .ToArray();
            bool supported = relevant.Length > 0 &&
                !relevant.Any(line =>
                    line.Contains(
                        "Unknown command",
                        StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(
                        "Command not found",
                        StringComparison.OrdinalIgnoreCase));
            probes.Add(new HlaeCameraCommandProbe(
                command,
                supported,
                relevant));
        }

        string version = File.Exists(options.HlaeExecutablePath)
            ? FileVersionInfo.GetVersionInfo(options.HlaeExecutablePath)
                .ProductVersion ?? "unknown"
            : "unknown";
        return new HlaeCameraCommandReport(
            DateTimeOffset.UtcNow,
            version,
            probes);
    }

    private static async Task PersistCameraCommandReportAsync(
        HlaeCameraCommandReport report,
        RenderWorkspace workspace,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(report, ReportJsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.State, "hlae-camera-command-report.json"),
            json,
            cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "hlae-camera-command-report.json"),
            json,
            cancellationToken);
    }

    private static async Task WaitForNetConReadyAsync(
        NetConsoleConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        TimeoutException? lastTimeout = null;
        int attempt = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            await connection.SendAsync(
                $"echo {NetConReadyMarker}",
                cancellationToken);
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            try
            {
                await connection.WaitForAsync(
                    NetConReadyMarker,
                    remaining < TimeSpan.FromSeconds(1)
                        ? remaining
                        : TimeSpan.FromSeconds(1),
                    cancellationToken);
                return;
            }
            catch (TimeoutException exception)
            {
                lastTimeout = exception;
            }
        }

        throw new TimeoutException(
            $"CS2 accepted the NetCon connection but did not execute console commands " +
            $"within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds " +
            $"after {attempt.ToString(CultureInfo.InvariantCulture)} readiness attempts.",
            lastTimeout);
    }

    private static async Task SeekToWarmupAsync(
        NetConsoleConnection connection,
        long warmupTick,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        TimeoutException? lastTimeout = null;
        long? lastReportedTick = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await connection.SendAsync("demo_pause", cancellationToken);
            await connection.SendAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"demo_gototick {warmupTick}"),
                cancellationToken);
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            try
            {
                IReadOnlyList<string> output =
                    await connection.ReadThroughAnyAsync(
                    [SeekFinishedMarker, SeekFlushedMarker],
                    remaining < TimeSpan.FromSeconds(5)
                        ? remaining
                        : TimeSpan.FromSeconds(5),
                    cancellationToken);
                lastReportedTick = output
                    .Select(line => SeekFinishedTickPattern.Match(line))
                    .Where(match => match.Success)
                    .Select(match => long.TryParse(
                        match.Groups["tick"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out long tick)
                            ? tick
                            : (long?)null)
                    .LastOrDefault(tick => tick.HasValue);
                if (lastReportedTick is long actualTick &&
                    Math.Abs(actualTick - warmupTick) <= 2)
                {
                    return;
                }
                await connection.SendAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"echo AFX_RENDER_SEEK_RETRY expected={warmupTick} actual={lastReportedTick?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}"),
                    cancellationToken);
            }
            catch (TimeoutException exception)
            {
                lastTimeout = exception;
                await Task.Delay(250, cancellationToken);
            }
        }
        throw new TimeoutException(
            $"Demo did not confirm seek to warmup tick {warmupTick} before timeout. " +
            $"Last reported tick: {lastReportedTick?.ToString(CultureInfo.InvariantCulture) ?? "none"}.",
            lastTimeout);
    }

    private static async Task WaitForDemoReadyAsync(
        NetConsoleConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        TimeoutException? lastTimeout = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await connection.SendAsync("status", cancellationToken);
            await connection.SendAsync(
                $"echo {DemoStatusEndMarker}",
                cancellationToken);
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            try
            {
                IReadOnlyList<string> output = await connection.ReadThroughAsync(
                    DemoStatusEndMarker,
                    remaining < TimeSpan.FromSeconds(2)
                        ? remaining
                        : TimeSpan.FromSeconds(2),
                    cancellationToken);
                bool demoConnected = output.Any(line =>
                    line.Contains("Connected [DEMO]", StringComparison.OrdinalIgnoreCase));
                bool gameLoopActive = output.Any(line =>
                    ActiveGameLoopPattern.IsMatch(line));
                if (demoConnected && gameLoopActive)
                {
                    return;
                }
            }
            catch (TimeoutException exception)
            {
                lastTimeout = exception;
            }
            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            "CS2 console became ready, but the requested demo did not reach both " +
            "Connected [DEMO] and the active game loop.",
            lastTimeout);
    }

    private sealed class NetConCaptureUiController(
        NetConsoleConnection connection) : ICaptureUiController
    {
        public async Task ApplyAsync(
            CapturePresentationMode mode,
            CancellationToken cancellationToken)
        {
            foreach (string command in CaptureUiProfileAdapter.GetCommands(mode))
                await connection.SendAsync(command, cancellationToken);
        }
    }

    public async Task QuitAsync(CancellationToken cancellationToken)
    {
        await using NetConsoleConnection connection = await ConnectAsync(
            logPath: null,
            TimeSpan.FromSeconds(options.ProcessShutdownTimeoutSeconds),
            cancellationToken);
        await connection.SendAsync("quit", cancellationToken);
    }

    public static ulong GetSteamId64(PlayerSelector player)
    {
        const ulong individualSteamId64Base = 76561197960265728UL;
        if (!ulong.TryParse(
                player.SteamId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong steamId64) ||
            steamId64 < individualSteamId64Base ||
            steamId64 > individualSteamId64Base + uint.MaxValue)
        {
            throw new InvalidOperationException(
                "player.steamId must be a valid individual SteamID64.");
        }

        return steamId64;
    }

    public static uint GetAccountId(ulong steamId64)
    {
        const ulong individualSteamId64Base = 76561197960265728UL;
        if (steamId64 < individualSteamId64Base ||
            steamId64 > individualSteamId64Base + uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steamId64),
                "SteamID64 must identify an individual Steam account.");
        }

        return checked((uint)(steamId64 - individualSteamId64Base));
    }

    public static string EscapeCommandArgument(string value)
    {
        if (value.Any(character => character is '\r' or '\n' or ';' or '\0'))
        {
            throw new ArgumentException("Player selector contains a forbidden console character.", nameof(value));
        }
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static async Task VerifySelectedPlayerAsync(
        NetConsoleConnection connection,
        ulong expectedSteamId64,
        CancellationToken cancellationToken)
    {
        const string endMarker = "AFX_RENDER_POV_VERIFY_END";
        await connection.SendAsync("spec_lock_to_accountid", cancellationToken);
        await connection.SendAsync($"echo {endMarker}", cancellationToken);
        IReadOnlyList<string> output = await connection.ReadThroughAsync(
            endMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        string steamIdText = expectedSteamId64.ToString(CultureInfo.InvariantCulture);
        if (!ContainsPlayerIdentity(output, expectedSteamId64))
        {
            await connection.SendAsync("quit", cancellationToken);
            throw new InvalidOperationException(
                $"CS2 selected a different POV. Expected SteamID64 {steamIdText}; " +
                $"spec_lock_to_accountid output: {string.Join(" | ", output)}");
        }
    }

    private static bool ContainsPlayerIdentity(
        IReadOnlyList<string> output,
        ulong expectedSteamId64)
    {
        uint accountId = GetAccountId(expectedSteamId64);
        string steamIdText = expectedSteamId64.ToString(CultureInfo.InvariantCulture);
        string accountIdText = accountId.ToString(CultureInfo.InvariantCulture);
        return output.Any(line =>
            Regex.IsMatch(
                line,
                $@"(?<!\d){Regex.Escape(steamIdText)}(?!\d)",
                RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                line,
                $@"(?<!\d){Regex.Escape(accountIdText)}(?!\d)",
                RegexOptions.CultureInvariant));
    }

    private async Task<NetConsoleConnection> ConnectAsync(
        string? logPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TcpClient client = new(AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, options.NetConPort, cancellationToken);
                return new NetConsoleConnection(client, logPath);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastError = exception;
                client.Dispose();
                await Task.Delay(250, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"CS2 NetCon did not accept connections on 127.0.0.1:{options.NetConPort}: {lastError?.Message}");
    }

    private sealed class NetConsoleConnection : IAsyncDisposable
    {
        private static readonly string[] FatalMarkers =
        [
            "NETWORK_DISCONNECT_MESSAGE_PARSE_ERROR",
            "Failed to parse message",
            "FATAL ERROR:",
            "Demo playback finished",
            "Starting recording ... FAILED",
            "AFXERROR:"
        ];

        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly StreamWriter? log;

        public NetConsoleConnection(TcpClient client, string? logPath)
        {
            this.client = client;
            NetworkStream stream = client.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false), true, 4096, leaveOpen: true);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            if (logPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                log = new StreamWriter(
                    new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
            }
        }

        public async Task SendAsync(string command, CancellationToken cancellationToken)
        {
            if (log is not null)
            {
                await log.WriteLineAsync($"> {command}".AsMemory(), cancellationToken);
            }
            await writer.WriteLineAsync(command.AsMemory(), cancellationToken);
        }

        public async Task WaitForAsync(
            string marker,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await ReadThroughAsync(marker, timeout, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ReadThroughAsync(
            string marker,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            await ReadThroughAnyAsync([marker], timeout, cancellationToken);

        public async Task<IReadOnlyList<string>> ReadThroughAnyAsync(
            IReadOnlyList<string> markers,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            List<string> lines = [];
            using CancellationTokenSource timeoutSource = new(timeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync(linked.Token);
                    if (line is null)
                    {
                        throw new IOException("CS2 closed the NetCon connection.");
                    }

                    line = line.Replace("\0", string.Empty, StringComparison.Ordinal);
                    lines.Add(line);
                    if (log is not null)
                    {
                        await log.WriteLineAsync(line.AsMemory(), cancellationToken);
                    }
                    if (FatalMarkers.Any(fatal => line.Contains(fatal, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException($"CS2 demo playback failed: {line}");
                    }
                    if (markers.Any(marker =>
                        line.Contains(marker, StringComparison.Ordinal)))
                    {
                        return lines;
                    }
                }
            }
            catch (OperationCanceledException) when (
                timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for CS2 console marker: {string.Join(" or ", markers)}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (log is not null)
            {
                await log.DisposeAsync();
            }
            await writer.DisposeAsync();
            reader.Dispose();
            client.Dispose();
        }
    }
}
