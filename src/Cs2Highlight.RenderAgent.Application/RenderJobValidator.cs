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
        else
        {
            if (job.Segment.TickRate is <= 0)
                errors.Add("segment.tickRate must be positive when provided.");
            if (job.Segment.RoundStartTick is < 0 ||
                job.Segment.RoundStartTick > job.Segment.StartTick)
                errors.Add("segment.roundStartTick must be between zero and startTick.");
            if (job.Segment.SafeEndTick is long safeEnd &&
                (safeEnd < job.Segment.StartTick || safeEnd > job.Segment.EndTick))
                errors.Add("segment.safeEndTick must be between startTick and endTick.");
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

        if (job.ContainsFirstPersonWeaponFire &&
            job.EffectivePresentationMode != CapturePresentationMode.PovCombat)
        {
            errors.Add(
                "WEAPON_HIDDEN_DURING_POV_COMBAT: render jobs containing first-person " +
                "weapon fire must use the PovCombat presentation mode.");
        }

        ValidateCamera(job, errors);

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
        if (environment.DemoInitializationStabilizationSeconds < 0)
        {
            errors.Add("RenderEnvironment demo initialization stabilization must not be negative.");
        }
        if (environment.Warmup.WarmupGameSeconds < 0 ||
            environment.Warmup.MinimumWallClockStabilizationSeconds < 0 ||
            environment.Warmup.MaximumGameplayReadyWaitSeconds <= 0)
        {
            errors.Add("RenderEnvironment warmup values are outside supported limits.");
        }
        if (environment.EnableClipStartQualityGate &&
            (environment.ClipStartSampleSeconds <= 0 ||
             environment.ClipStartBlackDurationSeconds <= 0 ||
             environment.ClipStartFreezeDurationSeconds <= 0))
        {
            errors.Add("RenderEnvironment clip-start quality values must be positive.");
        }

        ValidateOutput(job.OutputDirectory, environment.Cs2ExecutablePath, errors);
        return new ValidationReport(errors);
    }

    private static void ValidateCamera(RenderJob job, List<string> errors)
    {
        if (job.Camera is null)
        {
            errors.Add("camera is required.");
            return;
        }
        RenderCameraPlan camera = job.Camera;
        if (camera.Keyframes is null)
        {
            errors.Add("camera.keyframes is required.");
            return;
        }
        if (camera.Mode == RenderCameraMode.PlayerPov)
        {
            if (camera.Keyframes.Count > 0)
                errors.Add("PlayerPov camera must not contain keyframes.");
            return;
        }

        if (job.ContainsFirstPersonWeaponFire)
        {
            errors.Add(
                "Non-POV cameras cannot be used for first-person weapon-fire clips.");
        }
        if (job.EffectivePresentationMode == CapturePresentationMode.PovCombat)
        {
            errors.Add(
                "Non-POV cameras require a cinematic presentation mode.");
        }
        if (!camera.ManualSpikeVerified && !camera.CalibrationSpike)
        {
            errors.Add(
                "Non-POV camera requires manual-spike verification or an explicit calibration spike.");
        }
        if (string.IsNullOrWhiteSpace(camera.MapName))
            errors.Add("Non-POV camera requires mapName.");
        if (string.IsNullOrWhiteSpace(camera.HlaeVersionPrefix))
            errors.Add("Non-POV camera requires hlaeVersionPrefix.");
        if (camera.SafeVolume is null)
            errors.Add("Non-POV camera requires a calibrated safeVolume.");
        else if (!camera.SafeVolume.Minimum.IsFinite ||
                 !camera.SafeVolume.Maximum.IsFinite ||
                 camera.SafeVolume.Minimum.X > camera.SafeVolume.Maximum.X ||
                 camera.SafeVolume.Minimum.Y > camera.SafeVolume.Maximum.Y ||
                 camera.SafeVolume.Minimum.Z > camera.SafeVolume.Maximum.Z)
        {
            errors.Add("Camera safeVolume bounds are invalid.");
        }

        int requiredKeyframes = camera.Mode == RenderCameraMode.Static ? 1 : 4;
        if (camera.Keyframes.Count != requiredKeyframes)
        {
            errors.Add(
                $"{camera.Mode} camera requires exactly {requiredKeyframes} keyframe(s).");
        }
        long previousTick = -1;
        foreach (RenderCameraKeyframe keyframe in camera.Keyframes)
        {
            if (keyframe.Tick < job.Segment.StartTick ||
                keyframe.Tick > job.Segment.EndTick)
            {
                errors.Add("Camera keyframe ticks must stay inside the render segment.");
            }
            if (keyframe.Tick <= previousTick)
                errors.Add("Camera keyframe ticks must be strictly increasing.");
            previousTick = keyframe.Tick;
            if (!keyframe.Position.IsFinite ||
                !keyframe.Rotation.IsFinite ||
                !double.IsFinite(keyframe.Fov) ||
                keyframe.Fov is < 20 or > 140)
            {
                errors.Add("Camera keyframe transform or FOV is invalid.");
            }
            if (camera.SafeVolume is not null &&
                !camera.SafeVolume.Contains(keyframe.Position))
            {
                errors.Add("Camera keyframe is outside the calibrated safeVolume.");
            }
        }
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

            if (Directory.Exists(output) &&
                Directory.EnumerateFileSystemEntries(output).Any(IsUnexpectedOutputEntry))
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

    private static bool IsUnexpectedOutputEntry(string path)
    {
        string name = Path.GetFileName(path);
        return !string.Equals(name, "render-job.json", StringComparison.OrdinalIgnoreCase) &&
            !(string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase) &&
              Directory.Exists(path));
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeJobId();
}
