using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed record DemoUiDetectionReport(
    string SchemaVersion,
    bool Analyzed,
    bool DemoPlaybackStripDetected,
    int FramesAnalyzed,
    int FramesMatched,
    double? BoundaryRatio,
    double Confidence,
    IReadOnlyList<string> Evidence,
    string? Error);

public static class DemoUiDetector
{
    private const double MinimumPlaybackStripBoundaryCoverage = 0.60;
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const int AnalysisWidth = 320;
    public const int AnalysisHeight = 180;

    public static async Task<DemoUiDetectionReport> AnalyzeAsync(
        string ffmpegPath,
        string mediaPath,
        double sampleSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return Failed("FFMPEG_UNAVAILABLE_FOR_DEMO_UI_DETECTION");
        }
        ProcessStartInfo start = new()
        {
            FileName = Path.IsPathRooted(ffmpegPath) ||
                ffmpegPath.Contains(Path.DirectorySeparatorChar) ||
                ffmpegPath.Contains(Path.AltDirectorySeparatorChar)
                    ? Path.GetFullPath(ffmpegPath)
                    : ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-i", mediaPath,
            "-t", sampleSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-vf", $"fps=2,scale={AnalysisWidth}:{AnalysisHeight}:flags=area,format=gray",
            "-an", "-f", "rawvideo", "pipe:1"
        })
            start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
                return Failed("DEMO_UI_DETECTOR_DID_NOT_START");
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or
                UnauthorizedAccessException)
        {
            return Failed(
                "FFMPEG_UNAVAILABLE_FOR_DEMO_UI_DETECTION: " +
                exception.Message);
        }
        using MemoryStream bytes = new();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(
            bytes,
            cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(
            cancellationToken);
        await Task.WhenAll(
            process.WaitForExitAsync(cancellationToken),
            output);
        string diagnostic = await error;
        if (process.ExitCode != 0)
        {
            return Failed(
                "DEMO_UI_DETECTOR_FFMPEG_FAILED: " + diagnostic.Trim());
        }
        return AnalyzeGrayFrames(
            bytes.ToArray(),
            AnalysisWidth,
            AnalysisHeight);
    }

    public static DemoUiDetectionReport AnalyzeGrayFrames(
        ReadOnlySpan<byte> bytes,
        int width,
        int height)
    {
        int frameSize = checked(width * height);
        int frameCount = bytes.Length / frameSize;
        if (width < 32 || height < 32 || frameCount == 0)
            return Failed("DEMO_UI_DETECTOR_NO_FRAMES");
        List<int> matchedBoundaries = [];
        List<string> evidence = [];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            ReadOnlySpan<byte> frame = bytes.Slice(
                frameIndex * frameSize,
                frameSize);
            (bool matched, int boundary, double edge, double darkening,
                double coverage) = AnalyzeFrame(frame, width, height);
            if (!matched)
                continue;
            matchedBoundaries.Add(boundary);
            evidence.Add(FormattableString.Invariant(
                $"frame={frameIndex};boundary={boundary};edge={edge:0.00};darkening={darkening:0.00};coverage={coverage:0.000}"));
        }
        int dominantCount = 0;
        int? dominantBoundary = null;
        foreach (int boundary in matchedBoundaries.Distinct())
        {
            int count = matchedBoundaries.Count(value =>
                Math.Abs(value - boundary) <= 2);
            if (count > dominantCount)
            {
                dominantCount = count;
                dominantBoundary = boundary;
            }
        }
        int required = Math.Max(2, (int)Math.Ceiling(frameCount * 0.60));
        bool detected = dominantCount >= required;
        double confidence = frameCount == 0
            ? 0
            : Math.Clamp(dominantCount / (double)frameCount, 0, 1);
        return new DemoUiDetectionReport(
            "1.0",
            true,
            detected,
            frameCount,
            dominantCount,
            dominantBoundary.HasValue
                ? dominantBoundary.Value / (double)height
                : null,
            confidence,
            evidence,
            null);
    }

    public static async Task WriteAsync(
        string path,
        DemoUiDetectionReport report,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, Json),
            cancellationToken);
    }

    private static (bool Matched, int Boundary, double Edge,
        double Darkening, double Coverage) AnalyzeFrame(
            ReadOnlySpan<byte> frame,
            int width,
            int height)
    {
        int searchStart = (int)Math.Round(height * 0.70);
        int searchEnd = Math.Min(height - 5, (int)Math.Round(height * 0.94));
        int bestBoundary = searchStart;
        double bestEdge = 0;
        double bestCoverage = 0;
        double bestDarkening = double.MinValue;
        double bestScore = double.MinValue;
        bool foundMatch = false;
        for (int y = searchStart; y <= searchEnd; y++)
        {
            long difference = 0;
            int strong = 0;
            int row = y * width;
            int previous = (y - 1) * width;
            for (int x = 0; x < width; x++)
            {
                int delta = Math.Abs(frame[row + x] - frame[previous + x]);
                difference += delta;
                if (delta >= 12)
                    strong++;
            }
            double edge = difference / (double)width;
            double coverage = strong / (double)width;
            double above = Mean(
                frame,
                width,
                Math.Max(0, y - Math.Max(3, height / 12)),
                y - 1);
            double below = Mean(
                frame,
                width,
                y + 2,
                height - 2);
            double darkening = above - below;
            // The demo playback panel introduces a nearly full-width edge.
            // Gameplay HUD panels, weapon models and map geometry can create
            // equally dark lower regions, but their boundary is localized.
            bool matched = edge >= 9.0 &&
                coverage >= MinimumPlaybackStripBoundaryCoverage &&
                darkening >= 7.0;
            double score = edge + coverage * 10 + darkening * 0.1;
            if ((matched && !foundMatch) ||
                (matched == foundMatch && score > bestScore))
            {
                foundMatch = matched;
                bestScore = score;
                bestEdge = edge;
                bestBoundary = y;
                bestCoverage = coverage;
                bestDarkening = darkening;
            }
        }
        return (
            foundMatch,
            bestBoundary,
            bestEdge,
            bestDarkening == double.MinValue ? 0 : bestDarkening,
            bestCoverage);
    }

    private static double Mean(
        ReadOnlySpan<byte> frame,
        int width,
        int startRow,
        int endRow)
    {
        if (endRow < startRow)
            return 0;
        long sum = 0;
        int count = 0;
        for (int y = startRow; y <= endRow; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                sum += frame[row + x];
                count++;
            }
        }
        return count == 0 ? 0 : sum / (double)count;
    }

    private static DemoUiDetectionReport Failed(string error) => new(
        "1.0",
        false,
        false,
        0,
        0,
        null,
        0,
        [],
        error);
}
