using System.Text.Json;
using Cs2Highlight.Analysis;
using Microsoft.Extensions.Logging;

namespace Cs2Highlight.Cli;

internal sealed class RenderBatchCommand(ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<int> ExecuteAsync(string[] args)
    {
        if (!TryParse(args, out BatchCliOptions? options) || options is null)
        {
            PrintUsage();
            return 2;
        }
        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            return options.Resume
                ? await ResumeAsync(options, shutdown.Token)
                : await CreateAndRunAsync(options, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return 70;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return options.Resume ? 50 : 10;
        }
        catch (NotSupportedException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 20;
        }
        catch (AnalysisException exception)
        {
            Console.Error.WriteLine(exception.Error.Message);
            return exception.Error.Code == "PLAYER_NOT_FOUND" ? 12 : 10;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return options.Resume ? 50 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 99;
        }
    }

    private async Task<int> CreateAndRunAsync(
        BatchCliOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Output is null || options.SteamId is null ||
            (options.Demo is null && options.Highlights is null))
        {
            PrintUsage();
            return 2;
        }
        string root = Path.GetFullPath(options.Output);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!options.Overwrite)
            {
                Console.Error.WriteLine($"Batch output already exists: {root}");
                return 21;
            }
            EnsureSafeBatchRoot(root);
            Console.Error.WriteLine($"Overwriting existing batch directory: {root}");
            Directory.Delete(root, true);
        }
        Directory.CreateDirectory(Path.Combine(root, "logs"));

        string demoPath;
        HighlightsDocument highlights;
        if (options.Highlights is not null)
        {
            if (options.Demo is null)
            {
                Console.Error.WriteLine("--highlights requires --demo as the source demo path.");
                return 2;
            }
            demoPath = Path.GetFullPath(options.Demo);
            highlights = await LoadHighlightsAsync(options.Highlights, cancellationToken);
        }
        else
        {
            demoPath = Path.GetFullPath(options.Demo!);
            string analysisOutput = Path.Combine(root, "analysis");
            AnalysisPipeline pipeline = CreateAnalysisPipeline(options.ParserPath);
            AnalysisArtifacts artifacts = await pipeline.RunAsync(
                demoPath,
                analysisOutput,
                new HighlightDetectionOptions
                {
                    TargetPlayerId = options.SteamId,
                    PreRollSeconds = options.PreRoll,
                    PostRollSeconds = options.PostRoll
                },
                new RenderJobBuildOptions
                {
                    OutputRoot = analysisOutput,
                    Width = options.Batch.Width,
                    Height = options.Batch.Height,
                    Fps = options.Batch.Fps,
                    Fov = options.Batch.Fov,
                    TimeoutSeconds = options.Batch.TimeoutSeconds
                },
                cancellationToken);
            highlights = await LoadHighlightsAsync(artifacts.HighlightsPath, cancellationToken);
        }

        if (!File.Exists(demoPath) ||
            !string.Equals(Path.GetExtension(demoPath), ".dem", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Demo was not found or is not a .dem file: {demoPath}");
            return 10;
        }
        BatchPlanBuilder builder = new(new RenderJobBuilder(), TimeProvider.System);
        BatchPlanBuildResult build;
        try
        {
            build = builder.Build(
                demoPath,
                root,
                options.SteamId,
                highlights.Candidates,
                options.Batch);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 11;
        }
        JsonBatchStateStore store = new();
        await store.SaveAsync(Path.Combine(root, "batch-plan.json"), build.Plan, cancellationToken);
        foreach (BatchRenderItem item in build.Plan.Items)
        {
            Directory.CreateDirectory(Path.Combine(item.OutputDirectory, "logs"));
            await store.SaveAsync(
                item.RenderJobPath,
                build.RenderJobs[item.ItemId],
                cancellationToken);
        }
        await AppendLogAsync(
            root,
            $"Batch {build.Plan.BatchId} plan contains {build.Plan.Items.Count} items.",
            cancellationToken);
        if (options.DryRun)
        {
            Console.WriteLine(JsonSerializer.Serialize(build.Plan, JsonOptions));
            return 0;
        }
        return await ExecutePlanAsync(build.Plan, root, null, options.RenderAgentPath, cancellationToken);
    }

    private static async Task<int> ResumeAsync(
        BatchCliOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Output is null)
        {
            PrintUsage();
            return 2;
        }
        string root = Path.GetFullPath(options.Output);
        JsonBatchStateStore store = new();
        string planPath = Path.Combine(root, "batch-plan.json");
        string statePath = Path.Combine(root, "batch-state.json");
        if (!File.Exists(planPath) || !File.Exists(statePath))
        {
            Console.Error.WriteLine("Resume requires batch-plan.json and batch-state.json.");
            return 50;
        }
        BatchRenderPlan plan = await store.LoadAsync<BatchRenderPlan>(planPath, cancellationToken);
        BatchRenderState state = await store.LoadAsync<BatchRenderState>(statePath, cancellationToken);
        if (plan.SchemaVersion != "1.0" || state.SchemaVersion != "1.0")
        {
            Console.Error.WriteLine("Unsupported batch schema.");
            return 51;
        }
        await AppendLogAsync(root, $"Resuming batch {plan.BatchId}.", cancellationToken);
        return await ExecutePlanAsync(plan, root, state, options.RenderAgentPath, cancellationToken);
    }

    private static async Task<int> ExecutePlanAsync(
        BatchRenderPlan plan,
        string root,
        BatchRenderState? state,
        string renderAgentPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(renderAgentPath))
        {
            Console.Error.WriteLine($"Render Agent was not found: {renderAgentPath}");
            return 30;
        }
        await using BatchRenderLock batchLock = new();
        if (!batchLock.Acquired)
        {
            Console.Error.WriteLine("RENDERER_BUSY");
            return 31;
        }
        JsonBatchStateStore store = new();
        BatchRenderOrchestrator orchestrator = new(
            new ProcessRenderAgentClient(renderAgentPath),
            store,
            TimeProvider.System);
        BatchExecutionResult result = await orchestrator.RunAsync(
            plan,
            root,
            state,
            cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(result.Report, JsonOptions));
        return result.ExitCode;
    }

    private AnalysisPipeline CreateAnalysisPipeline(string parserPath) =>
        new(
            new GoCliDemoParser(parserPath, TimeSpan.FromMinutes(5)),
            new RuleBasedHighlightDetector(),
            new BestHighlightSelector(),
            new RenderJobBuilder(),
            TimeProvider.System,
            loggerFactory.CreateLogger<AnalysisPipeline>());

    private static async Task<HighlightsDocument> LoadHighlightsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        HighlightsDocument document = await JsonSerializer.DeserializeAsync<HighlightsDocument>(
            stream,
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("highlights.json is empty.");
        if (document.SchemaVersion != "1.0")
        {
            throw new InvalidDataException($"Unsupported highlights schema: {document.SchemaVersion}");
        }
        return document;
    }

    private static async Task AppendLogAsync(
        string root,
        string message,
        CancellationToken cancellationToken)
    {
        string line = $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}";
        await File.AppendAllTextAsync(
            Path.Combine(root, "logs", "batch-render.log"),
            line,
            cancellationToken);
    }

    private static void EnsureSafeBatchRoot(string root)
    {
        string pathRoot = Path.GetPathRoot(root)!;
        if (string.Equals(
                root.TrimEnd(Path.DirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) ||
            root.Length <= pathRoot.Length + 2)
        {
            throw new InvalidOperationException($"Unsafe overwrite target: {root}");
        }
    }

    private static bool TryParse(string[] args, out BatchCliOptions? result)
    {
        result = null;
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal)) return false;
            string name = token[2..];
            if (name is "resume" or "overwrite" or "dry-run")
            {
                flags.Add(name);
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }
            values[name] = args[++index];
        }
        bool resume = flags.Contains("resume");
        if (!values.TryGetValue("output", out string? output)) return false;
        string? steamId = values.GetValueOrDefault("steam-id");
        if (!resume && (steamId is null || steamId.Length != 17 || !steamId.All(char.IsAsciiDigit)))
        {
            return false;
        }
        if (!TryDouble(values, "min-score", 0, out double minimumScore) ||
            !TryInt(values, "max-clips", null, out int? maximumClips) ||
            !TryBool(values, "continue-on-error", true, out bool continueOnError) ||
            !TryInt(values, "max-retries", 1, out int? retries) ||
            !TryDouble(values, "overlap-threshold", 0.70, out double overlapThreshold) ||
            !TryInt(values, "width", 1920, out int? width) ||
            !TryInt(values, "height", 1080, out int? height) ||
            !TryInt(values, "fps", 60, out int? fps) ||
            !TryDouble(values, "fov", 90, out double fov) ||
            !TryInt(values, "timeout-seconds", 600, out int? timeout) ||
            !TryDouble(values, "pre-roll", 1, out double preRoll) ||
            !TryDouble(values, "post-roll", 1, out double postRoll))
        {
            return false;
        }
        if (!Enum.TryParse(values.GetValueOrDefault("sort-by", "Tick"), true, out BatchSortBy sortBy) ||
            !TryOrder(values.GetValueOrDefault("order", "asc"), out BatchSortOrder order) ||
            !Enum.TryParse(
                values.GetValueOrDefault("overlap-policy", "KeepHighestScore"),
                true,
                out OverlapResolutionPolicy overlapPolicy) ||
            !TryTypes(values.GetValueOrDefault("types"), out IReadOnlyList<HighlightType> types))
        {
            return false;
        }
        string defaultAgent = Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "Cs2Highlight.RenderAgent",
            "bin",
            "Release",
            "net8.0",
            "render-agent.exe"));
        string parser = values.GetValueOrDefault(
            "parser-path",
            Path.GetFullPath(Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "demo-parser",
                "demo-parser.exe")));
        result = new BatchCliOptions(
            values.GetValueOrDefault("demo"),
            values.GetValueOrDefault("highlights"),
            steamId,
            output,
            parser,
            values.GetValueOrDefault("render-agent-path", defaultAgent),
            flags.Contains("resume"),
            flags.Contains("overwrite"),
            flags.Contains("dry-run"),
            preRoll,
            postRoll,
            new BatchRenderOptions
            {
                MinimumScore = minimumScore,
                MaximumClips = maximumClips,
                ContinueOnError = continueOnError,
                MaxRetries = retries!.Value,
                OverlapThreshold = overlapThreshold,
                OverlapPolicy = overlapPolicy,
                SortBy = sortBy,
                SortOrder = order,
                Types = types,
                Width = width!.Value,
                Height = height!.Value,
                Fps = fps!.Value,
                Fov = fov,
                TimeoutSeconds = timeout!.Value
            });
        return true;
    }

    private static bool TryTypes(string? text, out IReadOnlyList<HighlightType> types)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            types = Enum.GetValues<HighlightType>();
            return true;
        }
        HashSet<HighlightType> parsed = [];
        foreach (string value in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(value, true, out HighlightType type))
            {
                types = [];
                return false;
            }
            parsed.Add(type);
        }
        types = parsed.OrderBy(type => type).ToArray();
        return parsed.Count > 0;
    }

    private static bool TryOrder(string text, out BatchSortOrder order)
    {
        if (string.Equals(text, "asc", StringComparison.OrdinalIgnoreCase))
        {
            order = BatchSortOrder.Ascending;
            return true;
        }
        if (string.Equals(text, "desc", StringComparison.OrdinalIgnoreCase))
        {
            order = BatchSortOrder.Descending;
            return true;
        }
        order = default;
        return false;
    }

    private static bool TryDouble(
        Dictionary<string, string> values,
        string name,
        double fallback,
        out double value) =>
        !values.TryGetValue(name, out string? text)
            ? (value = fallback) == fallback
            : double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);

    private static bool TryInt(
        Dictionary<string, string> values,
        string name,
        int? fallback,
        out int? value)
    {
        if (!values.TryGetValue(name, out string? text))
        {
            value = fallback;
            return true;
        }
        bool success = int.TryParse(text, out int parsed);
        value = success ? parsed : null;
        return success;
    }

    private static bool TryBool(
        Dictionary<string, string> values,
        string name,
        bool fallback,
        out bool value)
    {
        if (!values.TryGetValue(name, out string? text))
        {
            value = fallback;
            return true;
        }
        return bool.TryParse(text, out value);
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: cs2-highlight render-batch --demo <match.dem> --steam-id <SteamID64> " +
        "--output <directory> [--dry-run] [--max-clips N] [--resume]");

    private sealed record BatchCliOptions(
        string? Demo,
        string? Highlights,
        string? SteamId,
        string? Output,
        string ParserPath,
        string RenderAgentPath,
        bool Resume,
        bool Overwrite,
        bool DryRun,
        double PreRoll,
        double PostRoll,
        BatchRenderOptions Batch);
}
