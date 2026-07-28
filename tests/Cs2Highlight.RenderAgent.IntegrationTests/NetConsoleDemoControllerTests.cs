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
            DemoLoadTimeoutSeconds = 3
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
            new RenderSegment(100, 200),
            new VideoSettings(1920, 1080, 60, 90),
            workspace.Output,
            10);
        NetConsoleDemoController controller = new(options, new StateJournal(TimeProvider.System));

        await controller.ControlAsync(job, workspace, CancellationToken.None);
        await server;

        Assert.Contains("demo_gototick 100", commands);
        Assert.Contains("spec_player \"Player One\"", commands);
        Assert.Contains("mirv_streams record start", commands);
        Assert.Contains("demo_resume", commands);
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
        await writer.WriteLineAsync("CGameRules - paused on tick 1");

        while (true)
        {
            string? command = await reader.ReadLineAsync();
            if (command is null)
            {
                return;
            }
            commands.Add(command);
            if (command.StartsWith("demo_gototick ", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("[Demo] Demo Skipping finished at tick 100");
            }
            if (command == "demo_resume")
            {
                await writer.WriteLineAsync("AFX_RENDER_RECORDING_END");
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
