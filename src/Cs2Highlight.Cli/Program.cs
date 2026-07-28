using System.Text.Json;
using Cs2Highlight.Analysis;
using Microsoft.Extensions.Logging;

namespace Cs2Highlight.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out CliOptions? parsedOptions) || parsedOptions is null)
        {
            Console.Error.WriteLine(
                "Usage: cs2-highlight analyze --demo <match.dem> --output <directory> " +
                "[--steam-id <SteamID64>] [--parser-path <demo-parser.exe>] " +
                "[--pre-roll 3] [--post-roll 3]");
            return 2;
        }
        CliOptions options = parsedOptions;

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            }));
        AnalysisPipeline pipeline = new(
            new GoCliDemoParser(options.ParserPath, TimeSpan.FromMinutes(5)),
            new RuleBasedHighlightDetector(),
            new BestHighlightSelector(),
            new RenderJobBuilder(),
            TimeProvider.System,
            loggerFactory.CreateLogger<AnalysisPipeline>());
        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            AnalysisArtifacts artifacts = await pipeline.RunAsync(
                options.DemoPath,
                options.OutputDirectory,
                new HighlightDetectionOptions
                {
                    TargetPlayerId = options.SteamId,
                    PreRollSeconds = options.PreRoll,
                    PostRollSeconds = options.PostRoll
                },
                new RenderJobBuildOptions
                {
                    OutputRoot = options.OutputDirectory,
                    Width = options.Width,
                    Height = options.Height,
                    Fps = options.Fps,
                    Fov = options.Fov
                },
                shutdown.Token);
            Console.WriteLine(JsonSerializer.Serialize(artifacts, JsonOptions));
            if (artifacts.BestHighlight is null)
            {
                Console.Error.WriteLine("NO_HIGHLIGHTS_FOUND");
                return 20;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("""{"code":"CANCELLED","message":"Analysis was cancelled."}""");
            return 70;
        }
        catch (AnalysisException exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(exception.Error, JsonOptions));
            return exception.Error.Code switch
            {
                "DEMO_NOT_FOUND" or "PARSER_NOT_FOUND" => 10,
                "PLAYER_NOT_FOUND" => 11,
                "PARSER_TIMEOUT" => 21,
                "PARSER_FAILED" => 22,
                "NO_HIGHLIGHTS_FOUND" => 20,
                "OUTPUT_WRITE_FAILED" => 30,
                _ => 99
            };
        }
    }

    private static bool TryParse(string[] args, out CliOptions? options)
    {
        options = null;
        if (args.Length < 5 || !string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }
            values[args[index][2..]] = args[index + 1];
        }
        if (!values.TryGetValue("demo", out string? demo) ||
            !values.TryGetValue("output", out string? output))
        {
            return false;
        }

        string parserPath = values.GetValueOrDefault(
            "parser-path",
            Path.Combine(AppContext.BaseDirectory, "tools", "demo-parser.exe"));
        string? steamId = values.GetValueOrDefault("steam-id");
        if (steamId is not null &&
            (steamId.Length != 17 || !steamId.All(char.IsAsciiDigit)))
        {
            return false;
        }
        if (!TryNumber(values, "width", 1920, int.TryParse, out int width) ||
            !TryNumber(values, "height", 1080, int.TryParse, out int height) ||
            !TryNumber(values, "fps", 60, int.TryParse, out int fps) ||
            !TryNumber(values, "fov", 90d, double.TryParse, out double fov) ||
            !TryNumber(values, "pre-roll", 3d, double.TryParse, out double preRoll) ||
            !TryNumber(values, "post-roll", 3d, double.TryParse, out double postRoll))
        {
            return false;
        }
        options = new CliOptions(
            demo,
            output,
            parserPath,
            steamId,
            width,
            height,
            fps,
            fov,
            preRoll,
            postRoll);
        return true;
    }

    private delegate bool TryParseValue<T>(string? value, out T result);

    private static bool TryNumber<T>(
        Dictionary<string, string> values,
        string name,
        T defaultValue,
        TryParseValue<T> parser,
        out T result)
    {
        if (!values.TryGetValue(name, out string? text))
        {
            result = defaultValue;
            return true;
        }
        return parser(text, out result);
    }

    private sealed record CliOptions(
        string DemoPath,
        string OutputDirectory,
        string ParserPath,
        string? SteamId,
        int Width,
        int Height,
        int Fps,
        double Fov,
        double PreRoll,
        double PostRoll);
}
