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

        await controller.ControlAsync(
            job,
            workspace,
            DemoLoadMode.Start,
            CancellationToken.None);
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
        Assert.Equal(1, commands.Count(command => command == "demoui"));
        Assert.InRange(
            commands.FindIndex(command => command == "demoui"),
            playDemo + 1,
            commands.FindIndex(command => command == "demo_gototick 70") - 1);
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
        Assert.True(commands.Count(command => command == "cl_drawhud 1") >= 5);
        Assert.True(commands.Count(command => command == "cl_showdemooverlay 0") >= 5);
        Assert.True(commands.Count(command => command == "r_drawviewmodel 1") >= 5);
        Assert.True(commands.Count(command => command == "r_show_build_info 0") >= 5);
        Assert.True(commands.Count(command => command == "cl_trueview_show_status 0") >= 5);
        Assert.DoesNotContain("demoui false", commands);
        Assert.True(commands.Count(command => command == "hideconsole") >= 5);
        Assert.Contains(commands, command =>
            command.Contains("addAtTick 150", StringComparison.Ordinal) &&
            command.Contains("AFX_RENDER_SAFE_TAIL", StringComparison.Ordinal));
        Assert.Contains(commands, command =>
            command.Contains("addAtTick 200", StringComparison.Ordinal) &&
            command.Contains("mirv_streams record end", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReusesLoadedDemoWithoutSendingPlayDemo()
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
            "test-reuse",
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
        NetConsoleDemoController controller =
            new(options, new StateJournal(TimeProvider.System));

        await controller.ControlAsync(
            job,
            workspace,
            DemoLoadMode.ReuseCurrent,
            CancellationToken.None);
        await server;

        Assert.DoesNotContain(commands, command =>
            command.StartsWith("playdemo ", StringComparison.Ordinal));
        Assert.DoesNotContain("demoui", commands);
        Assert.DoesNotContain("demoui false", commands);
        Assert.Contains("status", commands);
        Assert.Contains("demo_gototick 70", commands);
        Assert.Contains("mirv_streams record start", commands);
    }

    [Fact]
    public async Task BuildsAndVerifiesFourKeyframeCampath()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<string> commands = [];
        Task server = RunFakeNetConAsync(
            listener,
            commands,
            campathTickDrift: 2);
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
        RenderCameraKeyframe[] keyframes =
        [
            Keyframe(100, 1, 2, 3, 80),
            Keyframe(133, 2, 3, 4, 79),
            Keyframe(166, 3, 4, 5, 78),
            Keyframe(200, 4, 5, 6, 77)
        ];
        RenderJob job = new(
            "campath-test",
            workspace.PreparedDemoPath,
            new PlayerSelector("76561198000000001", "Player One"),
            new RenderSegment(100, 200)
            {
                TickRate = 10,
                RoundStartTick = 0,
                LastKillTick = 150
            },
            new VideoSettings(1920, 1080, 60, 90),
            workspace.Output,
            10)
        {
            CaptureUi = CaptureUiProfile.Cinematic,
            PresentationMode = CapturePresentationMode.CinematicBroll,
            ContainsFirstPersonWeaponFire = false,
            Camera = new RenderCameraPlan
            {
                Mode = RenderCameraMode.Campath,
                MapName = "de_dust2",
                Keyframes = keyframes,
                SafeVolume = new RenderCameraBounds(
                    new RenderVector3(0, 0, 0),
                    new RenderVector3(10, 10, 10)),
                CalibrationSpike = true,
                VerificationId = "test",
                HlaeVersionPrefix = "unknown"
            }
        };
        NetConsoleDemoController controller =
            new(options, new StateJournal(TimeProvider.System));

        await controller.ControlAsync(
            job,
            workspace,
            DemoLoadMode.Start,
            CancellationToken.None);
        await server;

        Assert.Equal(4, commands.Count(value => value == "mirv_campath add"));
        Assert.Contains("mirv_campath enabled 1", commands);
        Assert.Contains("mirv_input end", commands);
        Assert.Contains("mirv_input fov 77", commands);
        Assert.True(File.Exists(
            Path.Combine(workspace.State, "applied-camera-report.json")));
    }

    private static RenderCameraKeyframe Keyframe(
        long tick,
        double x,
        double y,
        double z,
        double fov) =>
        new(
            tick,
            new RenderVector3(x, y, z),
            new RenderVector3(x, y, 0),
            fov);

    private static async Task RunFakeNetConAsync(
        TcpListener listener,
        List<string> commands,
        long campathTickDrift = 0)
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
        long currentTick = 0;
        double[] cameraPosition = [0, 0, 0];
        double[] cameraAngles = [0, 0, 0];
        double cameraFov = 90;
        List<(long Tick, double[] Position, double[] Angles, double Fov)>
            campath = [];
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
            if (command.StartsWith(
                    "echo AFX_RENDER_CAMERA_PROBE_",
                    StringComparison.Ordinal))
            {
                await writer.WriteLineAsync(command["echo ".Length..]);
            }
            if (command.StartsWith(
                    "echo AFX_RENDER_CAMPATH_ADD_",
                    StringComparison.Ordinal))
            {
                await writer.WriteLineAsync(command["echo ".Length..]);
            }
            if (command.StartsWith(
                    "echo AFX_RENDER_CAMERA_TRANSFORM_",
                    StringComparison.Ordinal))
            {
                await writer.WriteLineAsync(command["echo ".Length..]);
            }
            if (command == "echo AFX_RENDER_CAMPATH_PRINT_END")
            {
                await writer.WriteLineAsync("AFX_RENDER_CAMPATH_PRINT_END");
            }
            if (command.StartsWith("demo_gototick ", StringComparison.Ordinal))
            {
                seekAttempts++;
                string requestedTick = command.Split(' ', 2)[1];
                currentTick = long.Parse(
                    requestedTick,
                    System.Globalization.CultureInfo.InvariantCulture) -
                    campathTickDrift;
                await writer.WriteLineAsync(
                    seekAttempts == 1
                        ? "[Demo] Demo Skipping finished at tick 0"
                        : $"Demo Skipping flushing last 42 messages, tick {requestedTick} start 0 goal {requestedTick}");
            }
            if (command.StartsWith(
                    "mirv_input position ",
                    StringComparison.Ordinal))
            {
                cameraPosition = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(2)
                    .Select(value => double.Parse(
                        value,
                        System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
            }
            else if (command == "mirv_input position")
            {
                await writer.WriteLineAsync(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Current value: {cameraPosition[0]} {cameraPosition[1]} {cameraPosition[2]}"));
            }
            if (command.StartsWith(
                    "mirv_input angles ",
                    StringComparison.Ordinal))
            {
                cameraAngles = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(2)
                    .Select(value => double.Parse(
                        value,
                        System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
            }
            else if (command == "mirv_input angles")
            {
                await writer.WriteLineAsync(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Current value: {cameraAngles[0]} {cameraAngles[1]} {cameraAngles[2]}"));
            }
            if (command.StartsWith(
                    "mirv_input fov ",
                    StringComparison.Ordinal))
            {
                cameraFov = double.Parse(
                    command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2],
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (command == "mirv_input fov")
            {
                await writer.WriteLineAsync(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Current value: {cameraFov}"));
            }
            if (command == "mirv_campath add")
            {
                campath.Add((
                    currentTick,
                    [.. cameraPosition],
                    [.. cameraAngles],
                    cameraFov));
            }
            if (command == "mirv_campath print")
            {
                for (int index = 0; index < campath.Count; index++)
                {
                    var value = campath[index];
                    await writer.WriteLineAsync(string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"Y n {index} : {value.Tick} , 00m00s , 0.0 -> " +
                        $"( {value.Position[0]} {value.Position[1]} {value.Position[2]} ) " +
                        $"{value.Fov} ( {value.Angles[0]} {value.Angles[1]} {value.Angles[2]} )"));
                }
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
            if (command is "cl_showdemooverlay" or "spec_show_xray" or
                "r_show_build_info" or "cl_trueview_show_status")
            {
                await writer.WriteLineAsync($"\"{command}\" = \"false\"");
            }
            if (command is "cl_drawhud" or "r_drawviewmodel")
            {
                await writer.WriteLineAsync($"\"{command}\" = \"true\"");
            }
            if (command == "echo AFX_RENDER_PRESENTATION_VERIFY_END")
            {
                await writer.WriteLineAsync("AFX_RENDER_PRESENTATION_VERIFY_END");
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
