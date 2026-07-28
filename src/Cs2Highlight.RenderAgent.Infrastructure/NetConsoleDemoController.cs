using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

        string player = SelectPlayer(job.Player);
        await stateJournal.WriteAsync(
            workspace,
            RenderState.SelectingPlayer,
            $"Seek completed; selecting POV player '{player}'.",
            cancellationToken);
        await connection.SendAsync($"spec_player \"{EscapeCommandArgument(player)}\"", cancellationToken);
        await connection.SendAsync("spec_mode 4", cancellationToken);
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

    public static string SelectPlayer(PlayerSelector player) =>
        !string.IsNullOrWhiteSpace(player.Name)
            ? player.Name
            : throw new InvalidOperationException(
                "player.name is required because CS2 spec_player accepts a player name or observer slot, not SteamID64.");

    public static string EscapeCommandArgument(string value)
    {
        if (value.Any(character => character is '\r' or '\n' or ';' or '\0'))
        {
            throw new ArgumentException("Player selector contains a forbidden console character.", nameof(value));
        }
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
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
            "Demo playback finished"
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
                        return;
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
