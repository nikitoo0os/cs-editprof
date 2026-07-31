using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cs2Highlight.Music;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.Web.Services;

public sealed class CameraPreviewMediaAnalyzer(PipelineOptions options)
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);
    public async Task<CameraPreviewMetrics> AnalyzeAsync(
        string path,
        CameraShotPlan shot,
        CancellationToken cancellationToken)
    {
        string? executable = PipelinePathResolver.Resolve(options.FfmpegPath);
        if (executable is null)
            return Invalid(shot.TargetDurationSeconds);
        ProcessStartInfo start = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-nostats", "-i", path,
            "-t", Math.Min(10, shot.TargetDurationSeconds).ToString(
                "0.###",
                CultureInfo.InvariantCulture),
            "-an", "-vf",
            "fps=10,signalstats,metadata=print:key=lavfi.signalstats.YAVG,metadata=print:key=lavfi.signalstats.YDIF",
            "-f", "null", "-"
        })
            start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        if (!process.Start())
            return Invalid(shot.TargetDurationSeconds);
        Task<string> output = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string diagnostic = (await output) + '\n' + await error;
        double[] brightness = Values(
            diagnostic,
            "lavfi.signalstats.YAVG");
        double[] differences = Values(
            diagnostic,
            "lavfi.signalstats.YDIF");
        bool demoStrip = false;
        string reportPath = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            "demo-ui-detection-report.json");
        if (File.Exists(reportPath))
        {
            try
            {
                DemoUiDetectionReport? report = JsonSerializer.Deserialize<
                    DemoUiDetectionReport>(
                    await File.ReadAllTextAsync(
                        reportPath,
                        cancellationToken),
                    Json);
                demoStrip = report?.DemoPlaybackStripDetected == true;
            }
            catch (JsonException)
            {
                demoStrip = true;
            }
        }
        double mean = brightness.DefaultIfEmpty(0).Average();
        double variance = brightness.Length == 0
            ? 0
            : brightness.Average(value => Math.Pow(value - mean, 2));
        CameraPreviewMetrics media = new(
            shot.TargetDurationSeconds,
            mean / 255,
            brightness.Length == 0
                ? 1
                : brightness.Count(value => value < 5) /
                  (double)brightness.Length,
            variance / (255 * 255),
            differences.DefaultIfEmpty(0).Average() / 255,
            differences.DefaultIfEmpty(0).Max() / 255,
            differences.Length == 0
                ? 1
                : differences.Count(value => value < 1) /
                  (double)differences.Length,
            process.ExitCode == 0 && brightness.Length > 0)
        {
            DemoPlaybackStripDetected = demoStrip
        };
        return CameraGeometryMetricsBuilder.Enrich(shot, media);
    }

    private static double[] Values(string diagnostic, string key) =>
        Regex.Matches(
                diagnostic,
                $@"{Regex.Escape(key)}=(?<value>\d+(?:\.\d+)?)",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(value => double.Parse(
                value.Groups["value"].Value,
                CultureInfo.InvariantCulture))
            .ToArray();

    private static CameraPreviewMetrics Invalid(double duration) => new(
        duration,
        0,
        1,
        0,
        0,
        1,
        1,
        false);
}
