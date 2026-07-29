using System.Text.Json;
using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cs2Highlight.RenderAgent;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        bool single = args.Length == 3 &&
            string.Equals(args[0], "render", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "--job", StringComparison.OrdinalIgnoreCase);
        bool batch = args.Length == 3 &&
            string.Equals(args[0], "render-batch", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "--manifest", StringComparison.OrdinalIgnoreCase);
        if (!single && !batch)
        {
            Console.Error.WriteLine(
                "Usage: render-agent render --job <render-job.json> | " +
                "render-agent render-batch --manifest <render-batch.json>");
            return ExitCodes.InvalidArguments;
        }

        try
        {
            using IHost host = BuildHost(args);
            if (batch)
                return await RunBatchAsync(host, args[2]);
            string json = await File.ReadAllTextAsync(args[2]);
            RenderJob? job = JsonSerializer.Deserialize<RenderJob>(json, JsonOptions);
            if (job is null)
            {
                Console.Error.WriteLine("Render job JSON could not be parsed.");
                return ExitCodes.InvalidRenderJob;
            }

            using CancellationTokenSource shutdown = new();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Max(1, job.TimeoutSeconds)));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, timeout.Token);
            RenderOrchestrator orchestrator = host.Services.GetRequiredService<RenderOrchestrator>();
            var outcome = await orchestrator.RunAsync(job, linked.Token);
            Console.WriteLine(JsonSerializer.Serialize(outcome.Result, JsonOptions));
            return outcome.ExitCode;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Invalid render job JSON: {exception.Message}");
            return ExitCodes.InvalidRenderJob;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return ExitCodes.InvalidArguments;
        }
    }

    private static async Task<int> RunBatchAsync(IHost host, string manifestPath)
    {
        string json = await File.ReadAllTextAsync(manifestPath);
        RenderBatchManifest? manifest =
            JsonSerializer.Deserialize<RenderBatchManifest>(json, JsonOptions);
        if (manifest is null || manifest.RenderJobPaths.Count == 0)
        {
            Console.Error.WriteLine("Render batch manifest is empty.");
            return ExitCodes.InvalidArguments;
        }

        List<RenderJob> jobs = new(manifest.RenderJobPaths.Count);
        foreach (string jobPath in manifest.RenderJobPaths)
        {
            string jobJson = await File.ReadAllTextAsync(Path.GetFullPath(jobPath));
            RenderJob? job = JsonSerializer.Deserialize<RenderJob>(jobJson, JsonOptions);
            if (job is null)
            {
                Console.Error.WriteLine($"Render job JSON could not be parsed: {jobPath}");
                return ExitCodes.InvalidRenderJob;
            }
            jobs.Add(job);
        }

        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        int totalTimeout = (int)Math.Min(
            86400L,
            jobs.Sum(value => (long)value.TimeoutSeconds));
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(Math.Max(1, totalTimeout)));
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                shutdown.Token,
                timeout.Token);
        RenderSessionOrchestrator orchestrator =
            host.Services.GetRequiredService<RenderSessionOrchestrator>();
        IReadOnlyList<RenderJobOutcome> outcomes =
            await orchestrator.RunAsync(jobs, linked.Token);
        Console.WriteLine(JsonSerializer.Serialize(outcomes, JsonOptions));
        return outcomes.All(value => value.Result.Success)
            ? ExitCodes.Success
            : outcomes.First(value => !value.Result.Success).ExitCode;
    }

    private static IHost BuildHost(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables("CS2RENDER_");
        RenderEnvironmentOptions options = new();
        builder.Configuration.GetSection("RenderEnvironment").Bind(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IEnvironmentVerifier, EnvironmentVerifier>();
        builder.Services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        builder.Services.AddSingleton<IDemoCompatibilityRepairer, DemoCompatibilityRepairer>();
        builder.Services.AddSingleton<IRenderScriptGenerator, Source2ScriptGenerator>();
        builder.Services.AddSingleton<IProcessSupervisor, ProcessSupervisor>();
        builder.Services.AddSingleton<IHlaeLauncher, HlaeLauncher>();
        builder.Services.AddSingleton<IDemoController, NetConsoleDemoController>();
        builder.Services.AddSingleton<IRenderOutputWatcher, RenderOutputWatcher>();
        builder.Services.AddSingleton<IRenderLockFactory, RenderLockFactory>();
        builder.Services.AddSingleton<IStateJournal, StateJournal>();
        builder.Services.AddSingleton<RenderOrchestrator>();
        builder.Services.AddSingleton<RenderSessionOrchestrator>();
        return builder.Build();
    }
}
