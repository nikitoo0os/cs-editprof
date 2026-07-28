using System.Text.RegularExpressions;

namespace Cs2Highlight.RenderAgent.Application;

public static partial class RenderJobValidator
{
    public static ValidationReport Validate(RenderJob? job, RenderEnvironmentOptions environment)
    {
        List<string> errors = [];
        if (job is null)
        {
            return new ValidationReport(["Render job JSON is empty or invalid."]);
        }

        if (string.IsNullOrWhiteSpace(job.JobId) || !SafeJobId().IsMatch(job.JobId))
        {
            errors.Add("jobId must contain only letters, digits, dot, underscore, or dash (1-80 characters).");
        }

        if (string.IsNullOrWhiteSpace(job.DemoPath) ||
            !string.Equals(Path.GetExtension(job.DemoPath), ".dem", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(job.DemoPath))
        {
            errors.Add("demoPath must reference an existing .dem file.");
        }

        const ulong individualSteamId64Base = 76561197960265728UL;
        if (!ulong.TryParse(job.Player?.SteamId, out ulong steamId64) ||
            steamId64 < individualSteamId64Base ||
            steamId64 > individualSteamId64Base + uint.MaxValue)
        {
            errors.Add("player.steamId must be a valid individual SteamID64 for deterministic CS2 POV selection.");
        }

        if (job.Player?.SteamId is { Length: > 32 } || job.Player?.Name is { Length: > 128 })
        {
            errors.Add("Player identifiers exceed the allowed length.");
        }

        if (job.Segment is null || job.Segment.StartTick < 0 || job.Segment.EndTick <= job.Segment.StartTick)
        {
            errors.Add("segment requires startTick >= 0 and endTick > startTick.");
        }

        if (job.Video is null || job.Video.Width is < 320 or > 7680 || job.Video.Height is < 240 or > 4320 ||
            job.Video.Fps is < 1 or > 1000 || job.Video.Fov is < 1 or > 179)
        {
            errors.Add("video settings are outside supported limits (320x240..7680x4320, 1..1000 FPS, 1..179 FOV).");
        }

        if (job.TimeoutSeconds is < 1 or > 86400)
        {
            errors.Add("timeoutSeconds must be between 1 and 86400.");
        }

        if (environment.NetConPort is < 1 or > 65535)
        {
            errors.Add("RenderEnvironment.NetConPort must be between 1 and 65535.");
        }
        if (environment.ProcessStartupTimeoutSeconds < 1 ||
            environment.DemoLoadTimeoutSeconds < 1 ||
            environment.ProcessShutdownTimeoutSeconds < 1)
        {
            errors.Add("RenderEnvironment process and demo timeout values must be positive.");
        }

        ValidateOutput(job.OutputDirectory, environment.Cs2ExecutablePath, errors);
        return new ValidationReport(errors);
    }

    private static void ValidateOutput(string outputDirectory, string cs2ExecutablePath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            errors.Add("outputDirectory is required.");
            return;
        }

        try
        {
            string output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (File.Exists(output))
            {
                errors.Add("outputDirectory points to a file.");
            }

            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            {
                errors.Add("Existing outputDirectory is not empty; results are never overwritten.");
            }

            if (!string.IsNullOrWhiteSpace(cs2ExecutablePath))
            {
                string? cs2Root = Path.GetDirectoryName(Path.GetFullPath(cs2ExecutablePath));
                if (cs2Root is not null &&
                    (output.Equals(cs2Root, StringComparison.OrdinalIgnoreCase) ||
                     output.StartsWith(cs2Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("outputDirectory must not be inside the CS2 installation directory.");
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"outputDirectory is invalid: {exception.Message}");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeJobId();
}
