using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;

namespace Cs2Highlight.Web.Tests;

public sealed class Stage7FfmpegTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Stage7Ffmpeg")]
    public async Task DynamicEffectsRenderAndProbeWhenOptedIn()
    {
        string? configured = Environment.GetEnvironmentVariable("CS2_STAGE7_FFMPEG");
        if (string.IsNullOrWhiteSpace(configured))
            return;
        string ffmpeg = Path.GetFullPath(configured);
        string ffprobe = Path.Combine(
            Path.GetDirectoryName(ffmpeg)!,
            "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), ffmpeg);
        Assert.True(File.Exists(ffprobe), ffprobe);
        string outputRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable("CS2_STAGE7_FIXTURE_OUTPUT") ??
            Path.Combine("artifacts", "stage7-fixtures"));
        Directory.CreateDirectory(outputRoot);
        string source = Path.Combine(outputRoot, "effect-source.mp4");
        ProcessResult sourceResult = await RunAsync(
            ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=60:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", source
            ]);
        Assert.True(sourceResult.ExitCode == 0, sourceResult.Error);

        FfmpegCapabilityScanner scanner = new(
            new PipelineOptions { FfmpegPath = ffmpeg },
            TimeProvider.System);
        FfmpegCapabilities capabilities =
            await scanner.ScanAsync(CancellationToken.None);
        Assert.True(capabilities.Available, string.Join(" | ", capabilities.Warnings));
        Assert.Contains("scale", capabilities.Filters);
        await scanner.WriteAsync(
            capabilities,
            Path.Combine(outputRoot, "ffmpeg-capabilities.json"),
            CancellationToken.None);

        (string Name, EffectCue Cue)[] fixtures =
        [
            ("effect-smooth-zoom", Cue(VideoEffectType.SmoothZoom, Parameters(
                ("scale", 1.06), ("centerX", 0.5), ("centerY", 0.5),
                ("peakOffsetSeconds", 0.15)))),
            ("effect-punch-zoom", Cue(VideoEffectType.PunchZoom, Parameters(
                ("scale", 1.10), ("centerX", 0.54), ("centerY", 0.48),
                ("peakOffsetSeconds", 0.12)))),
            ("effect-crash-zoom", Cue(VideoEffectType.CrashZoom, Parameters(
                ("scale", 1.15), ("centerX", 0.5), ("centerY", 0.5),
                ("peakOffsetSeconds", 0.10)))),
            ("effect-micro-shake", Cue(VideoEffectType.MicroShake, Parameters(
                ("amplitudePixels", 5), ("impulses", 4)))),
            ("effect-motion-blur", Cue(VideoEffectType.DirectionalMotionBlur, Parameters(
                ("frames", 5)))),
            ("effect-frame-echo", Cue(VideoEffectType.FrameEcho, Parameters(
                ("frames", 4), ("opacity", 0.25)))),
            ("effect-rgb-split", Cue(VideoEffectType.RgbSplit, Parameters(
                ("redOffsetX", 4), ("blueOffsetX", -4)))),
            ("effect-hit-stop", Cue(VideoEffectType.HitStop, Parameters(
                ("frames", 4)))),
            ("effect-lens-warp", Cue(VideoEffectType.LensWarpPulse, Parameters(
                ("k1", -0.07)))),
            ("effect-roll-burst", Cue(VideoEffectType.RollBurst, Parameters(
                ("angleDegrees", 1.2)))),
            ("transition-whip-pan", Cue(VideoEffectType.WhipPan, Parameters(
                ("direction", 1)), start: 3.7, end: 3.9)),
            ("transition-flash-cut", Cue(VideoEffectType.FlashCut, Parameters(),
                start: 3.75, end: 3.85))
        ];

        DynamicEffectFilterGraphBuilder builder = new();
        List<object> report = [];
        foreach ((string name, EffectCue cue) in fixtures)
        {
            EffectCapabilityRequirement requirements =
                DynamicEffectPlanner.Requirements(cue.Type);
            string[] missing = requirements.RequiredFilters
                .Where(value => !capabilities.Supports(value))
                .ToArray();
            Assert.Empty(missing);
            DynamicEffectPlan plan = Plan(cue);
            DynamicFfmpegFilterGraph graph = builder.Build(
                "0:v:0",
                "0:a:0",
                4,
                plan,
                null,
                new VideoOutputOptions(1280, 720, 60),
                "aresample=48000",
                ["eq=contrast=1.02"]);
            string output = Path.Combine(outputRoot, $"{name}.mp4");
            Stopwatch watch = Stopwatch.StartNew();
            ProcessResult render = await RunAsync(
                ffmpeg,
                [
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", source,
                    "-filter_complex", graph.FilterComplex,
                    "-map", $"[{graph.VideoOutputLabel}]",
                    "-map", $"[{graph.AudioOutputLabel}]",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                    "-c:a", "aac", "-movflags", "+faststart", output
                ]);
            watch.Stop();
            Assert.True(render.ExitCode == 0, $"{name}: {render.Error}\n{graph.FilterComplex}");
            ProcessResult probe = await RunAsync(
                ffprobe,
                [
                    "-v", "error",
                    "-show_entries", "format=duration:stream=codec_type,width,height",
                    "-of", "json",
                    output
                ]);
            Assert.True(probe.ExitCode == 0, $"{name}: {probe.Error}");
            using JsonDocument metadata = JsonDocument.Parse(probe.Output);
            double duration = double.Parse(
                metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                CultureInfo.InvariantCulture);
            Assert.InRange(duration, 3.90, 4.10);
            Assert.True(new FileInfo(output).Length > 10_000, output);
            report.Add(new
            {
                name,
                cue.Type,
                renderDurationMilliseconds = watch.ElapsedMilliseconds,
                outputDurationSeconds = duration,
                outputSizeBytes = new FileInfo(output).Length,
                verified = true
            });
        }
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "fixture-report.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = "1.0",
                    capabilities.Version,
                    fixtures = report
                },
                JsonOptions));
    }

    private static EffectCue Cue(
        VideoEffectType type,
        IReadOnlyDictionary<string, double> parameters,
        double start = 1.8,
        double end = 2.1) =>
        new()
        {
            Id = $"fixture-{type.ToString().ToLowerInvariant()}",
            Type = type,
            Category = Category(type),
            Role = type is
                VideoEffectType.WhipPan or VideoEffectType.FlashCut
                    ? EffectRole.Transition
                    : EffectRole.Primary,
            StartSeconds = start,
            EndSeconds = end,
            Intensity = 0.6,
            Priority = 1,
            Seed = 42,
            Parameters = parameters
        };

    private static Dictionary<string, double> Parameters(
        params (string Name, double Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value);

    private static DynamicEffectPlan Plan(EffectCue cue) =>
        new()
        {
            SchemaVersion = "1.0",
            PlannerVersion = "7.0",
            GenerationId = "fixture",
            HighlightId = cue.Id,
            ClipId = cue.Id,
            Style = MovieStyle.Dynamic,
            Intensity = EffectIntensity.Balanced,
            DeterministicSeed = 42,
            Effects = [cue],
            RejectedEffects = [],
            Warnings = []
        };

    private static VideoEffectCategory Category(VideoEffectType type) => type switch
    {
        VideoEffectType.SmoothZoom or
        VideoEffectType.PunchZoom or
        VideoEffectType.CrashZoom => VideoEffectCategory.Zoom,
        VideoEffectType.MicroShake or
        VideoEffectType.RollBurst => VideoEffectCategory.Motion,
        VideoEffectType.DirectionalMotionBlur => VideoEffectCategory.Blur,
        VideoEffectType.FrameEcho => VideoEffectCategory.Temporal,
        VideoEffectType.RgbSplit => VideoEffectCategory.Color,
        VideoEffectType.HitStop => VideoEffectCategory.Time,
        VideoEffectType.LensWarpPulse => VideoEffectCategory.Distortion,
        _ => VideoEffectCategory.Transition
    };

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        Assert.True(process.Start());
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
