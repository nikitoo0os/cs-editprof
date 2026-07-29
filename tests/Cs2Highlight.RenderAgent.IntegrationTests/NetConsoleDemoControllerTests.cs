using System.Net;
using System.Net.Sockets;
using System.Text;
using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class NetConsoleDemoControllerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"netcon-controller-{Guid.NewGuid():N}");

    public NetConsoleDemoControllerTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "state"));
    }

    [Fact]
    public async Task WaitsForLoadThenSeeksSelectsAndRecords()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<string> commands = [];
        Task server = RunFakeNetConAsync(listener, commands);
        RenderEnvironmentOptions options = new()
        {
            NetConPort = port,
            ProcessStartupTimeoutSeconds = 3,
            DemoLoadTimeoutSeconds = 3,
            DemoInitializationStabilizationSeconds = 0,
            Warmup = new RenderWarmupOptions
            {
                WarmupGameSeconds = 3,
                MinimumWallClockStabilizationSeconds = 0,
                MaximumGameplayReadyWaitSeconds = 3
            }
        };
        RenderWorkspace workspace = new(
            root,
            Path.Combine(root, "input"),
            Path.Combine(root, "config"),
            Path.Combine(root, "raw"),
            Path.Combine(root, "output"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "state"),
            Path.Combine(root, "input", "demo.dem"));
        RenderJob job = new(
            "test",
            workspace.PreparedDemoPath,
            new PlayerSelector("76561198000000001", "Player One"),
            new RenderSegment(100, 200)
            {
                TickRate = 10,
                RoundStartTick = 0,
                PrimaryKillTick = 150,
                LastKillTick = 150,
                SafeEndTick = 200
            },
            new VideoSettings(1920, 1080, 60, 90),
            workspace.Output,
            10);
        NetConsoleDemoController controller = new(options, new StateJournal(TimeProvider.System));

        await controller.ControlAsync(job, workspace, CancellationToken.None);
        await server;

        Assert.Contains("demo_gototick 70", commands);
        Assert.Contains(commands, command =>
            command.StartsWith("playdemo \"", StringComparison.Ordinal));
        int playDemo = commands.FindIndex(command =>
            command.StartsWith("playdemo \"", StringComparison.Ordinal));
        int sessionReset = commands.FindIndex(command =>
            command == "mirv_cmd clear");
        Assert.InRange(sessionReset, 0, playDemo - 1);
        Assert.Contains(commands, command =>
            command.StartsWith(
                "mirv_streams record name \"",
                StringComparison.Ordinal));
        Assert.True(
            commands.FindIndex(command =>
                command.StartsWith("playdemo \"", StringComparison.Ordinal)) <
            commands.FindIndex(command => command == "demo_gototick 70"));
        Assert.True(commands.Count(command =>
            command == "echo AFX_RENDER_NETCON_READY") >= 2);
        Assert.True(commands.Count(command => command == "status") >= 2);
        Assert.True(commands.Count(command => command == "demo_gototick 70") >= 2);
        Assert.Contains(commands, command =>
            command.Contains("addAtTick 100", StringComparison.Ordinal) &&
            command.Contains("AFX_RENDER_START_READY", StringComparison.Ordinal));
        int startSchedule = commands.FindIndex(command =>
            command.Contains("addAtTick 100", StringComparison.Ordinal) &&
            command.Contains("AFX_RENDER_START_READY", StringComparison.Ordinal));
        int clearStartSchedule = commands.FindIndex(
            startSchedule + 1,
            command => command == "mirv_cmd clear");
        int endSchedule = commands.FindIndex(command =>
            command.Contains("addAtTick 200", StringComparison.Ordinal) &&
            command.Contains("AFX_RENDER_RECORDING_END", StringComparison.Ordinal));
        Assert.True(clearStartSchedule > startSchedule);
        Assert.True(endSchedule > clearStartSchedule);
        Assert.Contains("mirv_cvar_unhide_all", commands);
        Assert.Contains("spec_mode 1", commands);
        Assert.Contains("spec_lock_to_accountid 39734273", commands);
        Assert.DoesNotContain(commands, command =>
            command.StartsWith("spec_player ", StringComparison.Ordinal));
        Assert.Contains("mirv_streams record start", commands);
        Assert.Contains("demo_resume", commands);
        Assert.Equal(4, commands.Count(command => command == "cl_drawhud 1"));
        Assert.Equal(4, commands.Count(command => command == "hideconsole"));
        Assert.Contains(commands, command =>
            command.Contains("addAtTick 150", StringComparison.Ordinal) &&
            command.Contains("AFX_RENDER_SAFE_TAIL", StringComparison.Ordinal));
        Assert.Contains(commands, command =>
            command.Contains("addAtTick 200", StringComparison.Ordinal) &&
            command.Contains("mirv_streams record end", StringComparison.Ordinal));
    }

    private static async Task RunFakeNetConAsync(TcpListener listener, List<string> commands)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, new UTF8Encoding(false), leaveOpen: true);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        bool warmedUp = false;
        bool startPauseScheduled = false;
        int readinessAttempts = 0;
        int demoStatusAttempts = 0;
        int seekAttempts = 0;
        while (true)
        {
            string? command = await reader.ReadLineAsync();
            if (command is null)
            {
                return;
            }
            commands.Add(command);
            if (command.Contains("addAtTick 100", StringComparison.Ordinal) &&
                command.Contains("AFX_RENDER_START_READY", StringComparison.Ordinal))
            {
                startPauseScheduled = true;
            }
            if (command == "mirv_cmd clear")
            {
                startPauseScheduled = false;
            }
            if (command == "echo AFX_RENDER_NETCON_READY")
            {
                readinessAttempts++;
                if (readinessAttempts >= 2)
                {
                    await writer.WriteLineAsync("AFX_RENDER_NETCON_READY");
                }
            }
            if (command == "status")
            {
                demoStatusAttempts++;
                await writer.WriteLineAsync("Client: Connected [DEMO]");
                await writer.WriteLineAsync(
                    demoStatusAttempts == 1
                        ? "@ Current  :  levelload"
                        : "@ Current  :  game");
            }
            if (command == "echo AFX_RENDER_DEMO_STATUS_END")
            {
                await writer.WriteLineAsync("AFX_RENDER_DEMO_STATUS_END");
            }
            if (command.StartsWith("demo_gototick ", StringComparison.Ordinal))
            {
                seekAttempts++;
                string requestedTick = command.Split(' ', 2)[1];
                await writer.WriteLineAsync(
                    seekAttempts == 1
                        ? "[Demo] Demo Skipping finished at tick 0"
                        : $"[Demo] Demo Skipping finished at tick {requestedTick}");
            }
            if (command == "spec_lock_to_accountid")
            {
                await writer.WriteLineAsync("\"spec_lock_to_accountid\" = \"39734273\"");
            }
            if (command == "echo AFX_RENDER_POV_VERIFY_END")
            {
                await writer.WriteLineAsync("AFX_RENDER_POV_VERIFY_END");
            }
            if (command == "echo AFX_RENDER_CAPTURE_PROFILE_APPLIED")
            {
                await writer.WriteLineAsync("AFX_RENDER_CAPTURE_PROFILE_APPLIED");
            }
            if (command == "demo_resume")
            {
                if (!warmedUp)
                {
                    warmedUp = true;
                    await writer.WriteLineAsync("AFX_RENDER_START_READY");
                    continue;
                }
                if (startPauseScheduled)
                {
                    await writer.WriteLineAsync(
                        "AFXERROR: start-tick pause was triggered again");
                    continue;
                }
                await writer.WriteLineAsync("AFX_RENDER_RECORDING_END");
                await writer.WriteLineAsync("AFX_RENDER_SAFE_TAIL");
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
