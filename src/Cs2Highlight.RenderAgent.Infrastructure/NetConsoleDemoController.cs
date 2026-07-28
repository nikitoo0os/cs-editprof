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
    private const string DemoReadyMarker = "CGameRules - paused on tick";
    private const string SeekFinishedMarker = "Demo Skipping finished at tick";
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

        await stateJournal.WriteAsync(
            workspace,
            RenderState.LoadingDemo,
            "Connected to CS2 NetCon; waiting for demo initialization.",
            cancellationToken);
        await connection.WaitForAsync(
            DemoReadyMarker,
            TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
            cancellationToken);

        await stateJournal.WriteAsync(
            workspace,
            RenderState.Seeking,
            $"Demo initialized; seeking to tick {job.Segment.StartTick}.",
            cancellationToken);
        await connection.SendAsync("demo_pause", cancellationToken);
        await connection.SendAsync(
            string.Create(CultureInfo.InvariantCulture, $"demo_gototick {job.Segment.StartTick}"),
            cancellationToken);
        await connection.WaitForAsync(
            SeekFinishedMarker,
            TimeSpan.FromSeconds(options.DemoLoadTimeoutSeconds),
            cancellationToken);
        await connection.SendAsync("demo_pause", cancellationToken);

        ulong steamId64 = GetSteamId64(job.Player);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.SelectingPlayer,
            $"Seek completed; locking POV to SteamID64 {steamId64}.",
            cancellationToken);
        await connection.SendAsync("mirv_cvar_unhide_all", cancellationToken);
        await connection.SendAsync("spec_lock_to_accountid 0", cancellationToken);
        await connection.SendAsync("spec_mode 1", cancellationToken);
        int playerSlot;
        try
        {
            playerSlot = await ResolvePlayerSlotAsync(connection, steamId64, cancellationToken);
        }
        catch
        {
            await connection.SendAsync("quit", cancellationToken);
            throw;
        }
        await connection.SendAsync($"spec_player {playerSlot}", cancellationToken);
        await connection.SendAsync("spec_lock_to_current_player", cancellationToken);
        await VerifySelectedPlayerAsync(connection, steamId64, cancellationToken);
        await connection.SendAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mirv_cmd addAtTick {job.Segment.EndTick} \"mirv_streams record end; demo_pause; echo {RecordingEndMarker}\""),
            cancellationToken);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.Recording,
            $"Starting recording through tick {job.Segment.EndTick}.",
            cancellationToken);
        await connection.SendAsync("mirv_streams record start", cancellationToken);
        await connection.SendAsync("echo AFX_RENDER_RECORDING_START", cancellationToken);
        await connection.SendAsync("demo_resume", cancellationToken);

        await connection.WaitForAsync(
            RecordingEndMarker,
            TimeSpan.FromSeconds(job.TimeoutSeconds),
            cancellationToken);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.StoppingRecording,
            $"Recording stopped at tick {job.Segment.EndTick}.",
            cancellationToken);
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

    public static int ResolvePlayerSlot(IReadOnlyList<string> statusOutput, ulong steamId64)
    {
        const ulong individualSteamId64Base = 76561197960265728UL;
        uint accountId = checked((uint)(steamId64 - individualSteamId64Base));
        string steamId64Text = steamId64.ToString(CultureInfo.InvariantCulture);
        string steam3Text = $"[U:1:{accountId.ToString(CultureInfo.InvariantCulture)}]";
        string steam2Suffix = string.Create(
            CultureInfo.InvariantCulture,
            $"{accountId % 2}:{accountId / 2}");

        foreach (string line in statusOutput)
        {
            bool isTarget =
                Regex.IsMatch(
                    line,
                    $@"(?<!\d){Regex.Escape(steamId64Text)}(?!\d)",
                    RegexOptions.CultureInvariant) ||
                line.Contains(steam3Text, StringComparison.OrdinalIgnoreCase) ||
                line.Contains($"STEAM_0:{steam2Suffix}", StringComparison.OrdinalIgnoreCase) ||
                line.Contains($"STEAM_1:{steam2Suffix}", StringComparison.OrdinalIgnoreCase);
            if (!isTarget)
            {
                continue;
            }

            Match prefix = Regex.Match(
                line,
                @"^\s*#?\s*(?<userId>\d+)(?:\s+(?<slot>\d+))?\s+""",
                RegexOptions.CultureInvariant);
            if (!prefix.Success)
            {
                throw new InvalidOperationException(
                    $"Found SteamID64 {steamId64Text} in CS2 status, but could not parse its player slot: {line}");
            }

            int userId = int.Parse(prefix.Groups["userId"].Value, CultureInfo.InvariantCulture);
            int slot = prefix.Groups["slot"].Success
                ? int.Parse(prefix.Groups["slot"].Value, CultureInfo.InvariantCulture)
                : checked(userId + 1);
            if (slot is < 1 or > 64)
            {
                throw new InvalidOperationException(
                    $"CS2 returned invalid player slot {slot} for SteamID64 {steamId64Text}: {line}");
            }

            return slot;
        }

        throw new InvalidOperationException(
            $"SteamID64 {steamId64Text} was not found in CS2 demo status. " +
            $"Status output: {string.Join(" | ", statusOutput)}");
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

    private static async Task<int> ResolvePlayerSlotAsync(
        NetConsoleConnection connection,
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        const string startMarker = "AFX_RENDER_PLAYER_STATUS_START";
        const string endMarker = "AFX_RENDER_PLAYER_STATUS_END";
        await connection.SendAsync($"echo {startMarker}", cancellationToken);
        await connection.SendAsync("status", cancellationToken);
        await connection.SendAsync($"echo {endMarker}", cancellationToken);
        await connection.ReadThroughAsync(
            startMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        IReadOnlyList<string> statusOutput = await connection.ReadThroughAsync(
            endMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        return ResolvePlayerSlot(statusOutput, steamId64);
    }

    private static async Task VerifySelectedPlayerAsync(
        NetConsoleConnection connection,
        ulong expectedSteamId64,
        CancellationToken cancellationToken)
    {
        const string endMarker = "AFX_RENDER_POV_VERIFY_END";
        uint accountId = checked((uint)(expectedSteamId64 - 76561197960265728UL));
        await connection.SendAsync("spec_lock_to_accountid", cancellationToken);
        await connection.SendAsync($"echo {endMarker}", cancellationToken);
        IReadOnlyList<string> output = await connection.ReadThroughAsync(
            endMarker,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        string steamIdText = expectedSteamId64.ToString(CultureInfo.InvariantCulture);
        string accountIdText = accountId.ToString(CultureInfo.InvariantCulture);
        if (!output.Any(line =>
                line.Contains(steamIdText, StringComparison.Ordinal) ||
                line.Contains(accountIdText, StringComparison.Ordinal)))
        {
            await connection.SendAsync("quit", cancellationToken);
            throw new InvalidOperationException(
                $"CS2 selected a different POV. Expected SteamID64 {steamIdText}; " +
                $"spec_lock_to_accountid output: {string.Join(" | ", output)}");
        }
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
