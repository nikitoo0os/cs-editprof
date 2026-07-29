using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class NetConsoleDemoController(
    RenderEnvironmentOptions options,
    IStateJournal stateJournal) : IDemoController
{
    private static readonly Regex SeekFinishedTickPattern = new(
        @"Demo Skipping finished at tick\s+(?<tick>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex ActiveGameLoopPattern = new(
        @"@\s*Current\s*:\s*game\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private const string NetConReadyMarker = "AFX_RENDER_NETCON_READY";
    private const string DemoStatusEndMarker = "AFX_RENDER_DEMO_STATUS_END";
    private const string SeekFinishedMarker = "Demo Skipping finished at tick";
    private const string StartReadyMarker = "AFX_RENDER_START_READY";
    private const string SafeTailMarker = "AFX_RENDER_SAFE_TAIL";
    private const string RecordingEndMarker = "AFX_RENDER_RECORDING_END";

    public async Task ControlAsync(
        RenderJob job,
        RenderWorkspace workspace,
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
        await captureUi.ApplyAsync(job.CaptureUi, cancellationToken);
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

        long warmupTick = ComputeWarmupTick(job.Segment, options.Warmup);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.SeekingToWarmup,
            $"Demo initialized; seeking to warmup tick {warmupTick}.",
            cancellationToken);
        await SeekToWarmupAsync(
            connection,
            warmupTick,
            TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
            cancellationToken);
        await connection.SendAsync("demo_pause", cancellationToken);
        await captureUi.ApplyAsync(job.CaptureUi, cancellationToken);

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
        await captureUi.ApplyAsync(job.CaptureUi, cancellationToken);

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
            $"Applying {job.CaptureUi} UI profile ({CaptureUiProfileAdapter.TemplateVersion}).",
            cancellationToken);
        if (options.Warmup.ReapplyCaptureProfileAfterWarmup)
            await captureUi.ApplyAsync(job.CaptureUi, cancellationToken);
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
        // commands and output path. Reset both before loading the demo again.
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
                    await connection.ReadThroughAsync(
                    SeekFinishedMarker,
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
            CaptureUiProfile profile,
            CancellationToken cancellationToken)
        {
            foreach (string command in CaptureUiProfileAdapter.GetCommands(profile))
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
                    new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read),
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
                    if (line.Contains(marker, StringComparison.Ordinal))
                    {
                        return lines;
                    }
                }
            }
            catch (OperationCanceledException) when (
                timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for CS2 console marker: {marker}");
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
