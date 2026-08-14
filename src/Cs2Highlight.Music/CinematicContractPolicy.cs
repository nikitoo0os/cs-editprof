namespace Cs2Highlight.Music;

public sealed record CinematicContractValidationReport(
    string ContractVersion,
    bool IsValid,
    IReadOnlyDictionary<string, bool> Checks,
    IReadOnlyList<string> Violations)
{
    public static CinematicContractValidationReport Valid(
        IReadOnlyDictionary<string, bool> checks) =>
        new(CinematicContractPolicy.ContractVersion, true, checks, []);
}

/// <summary>
/// Executable acceptance policy for the Cinematic Director film contract.
/// This validator deliberately contains only deterministic plan/media rules;
/// FFmpeg-specific probing remains in the web compilation service.
/// </summary>
public static class CinematicContractPolicy
{
    public const string ContractVersion = "10.9";
    public const double StandardMaximumMovieDurationSeconds = 60;
    public const double MaximumMovieDurationSeconds = 180;
    public const double MinimumFreeCameraShotSeconds = 1.5;
    public const double MinimumOrdinaryShotSeconds = 0.25;
    public const double TargetIntegratedLoudnessLufs = -14;
    public const double TargetLoudnessRangeLu = 7;
    public const double MaximumTruePeakDb = -0.8;

    private static readonly HashSet<string> SupportedEffects =
    [
        "SmoothZoom",
        "PunchZoom",
        "CrashZoom",
        "OffsetZoom",
        "MicroShake",
        "RecoilShake",
        "DirectionalMotionBlur",
        "ZoomBlur",
        "FrameEcho",
        "RgbSplit",
        "HitStop",
        "LensWarpPulse",
        "RollBurst",
        "FlashAccent",
        "VignettePulse",
        "HardCut",
        "FlashCut",
        "FadeTransition",
        "WhipPan",
        "WhipZoom"
    ];

    public static CinematicContractValidationReport ValidatePlan(
        CinematicMoviePlan plan,
        int fps = 60)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int safeFps = Math.Max(1, fps);
        double tolerance = 1d / safeFps;
        Dictionary<string, bool> checks = new(StringComparer.Ordinal);
        List<string> violations = [];

        bool version = IsContractVersion(plan.PlannerVersion);
        checks["planner_version"] = version;
        AddViolation(violations, version, "PLANNER_VERSION_BELOW_CONTRACT");

        bool duration = double.IsFinite(plan.TargetDurationSeconds) &&
            plan.TargetDurationSeconds > 0 &&
            plan.TargetDurationSeconds <= MaximumMovieDurationSeconds + tolerance;
        checks["duration"] = duration;
        AddViolation(violations, duration, "CINEMATIC_DURATION_OUTSIDE_CONTRACT");

        CinematicSequenceSegment[] segments = plan.Segments
            .OrderBy(value => value.OutputStartSeconds)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        bool nonEmpty = segments.Length > 0;
        checks["segments_present"] = nonEmpty;
        AddViolation(violations, nonEmpty, "CINEMATIC_TIMELINE_EMPTY");

        bool continuous = nonEmpty &&
            Math.Abs(segments[0].OutputStartSeconds) <= tolerance &&
            Math.Abs(segments[^1].OutputEndSeconds -
                     plan.TargetDurationSeconds) <= tolerance &&
            segments.All(value =>
                double.IsFinite(value.OutputStartSeconds) &&
                double.IsFinite(value.OutputEndSeconds) &&
                value.OutputEndSeconds - value.OutputStartSeconds >=
                    MinimumOrdinaryShotSeconds - tolerance) &&
            segments.Zip(segments.Skip(1)).All(pair =>
                Math.Abs(pair.Second.OutputStartSeconds -
                         pair.First.OutputEndSeconds) <= tolerance);
        checks["continuous_timeline"] = continuous;
        AddViolation(violations, continuous, "CINEMATIC_TIMELINE_DISCONTINUITY");

        bool brollCameraSafe = true;
        bool brollDurationSafe = true;
        bool noPersistedPovFallback = true;
        foreach (CinematicSequenceSegment segment in segments)
        {
            bool isNonHighlight = segment.HighlightId is null;
            if (!isNonHighlight)
                continue;

            bool freeCamera = segment.Camera.Family != CameraShotFamily.PlayerPov &&
                segment.Camera.Type != CameraShotType.PlayerPov;
            brollCameraSafe &= freeCamera;
            brollDurationSafe &= segment.OutputEndSeconds -
                segment.OutputStartSeconds >=
                MinimumFreeCameraShotSeconds - tolerance;
            noPersistedPovFallback &= !segment.Camera.Warnings.Any(value =>
                value.Contains("POV_FALLBACK", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("POV_SUBSTITUTION", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("PERSISTED_POV", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("PREVIEW_PENDING", StringComparison.OrdinalIgnoreCase));
        }
        checks["broll_camera_is_cinematic"] = brollCameraSafe;
        checks["broll_duration"] = brollDurationSafe;
        checks["no_persisted_pov_fallback"] = noPersistedPovFallback;
        AddViolation(violations, brollCameraSafe, "CINEMATIC_BROLL_POV_FORBIDDEN");
        AddViolation(violations, brollDurationSafe, "CINEMATIC_BROLL_SHOT_TOO_SHORT");
        AddViolation(violations, noPersistedPovFallback, "PERSISTED_POV_FALLBACK_REUSED");

        bool highlightMatches = segments
            .Where(value => value.HighlightId is not null)
            .Select(value => value.HighlightId!)
            .Distinct(StringComparer.Ordinal)
            .All(id => plan.HighlightMatches.Any(value =>
                string.Equals(value.HighlightId, id, StringComparison.Ordinal)));
        checks["highlight_matches"] = highlightMatches;
        AddViolation(violations, highlightMatches, "CINEMATIC_HIGHLIGHT_MATCH_MISSING");

        bool effectsSafe = true;
        List<double> rgbSplitTimes = [];
        foreach (CinematicSequenceSegment segment in segments)
        {
            double durationSeconds = segment.OutputEndSeconds -
                segment.OutputStartSeconds;
            foreach (MotivatedEffectDirective effect in segment.Effects)
            {
                bool supported = SupportedEffects.Contains(effect.EffectType);
                bool finite = double.IsFinite(effect.StartSeconds) &&
                    double.IsFinite(effect.EndSeconds) &&
                    double.IsFinite(effect.Intensity);
                bool inside = effect.StartSeconds >= -tolerance &&
                    effect.EndSeconds <= durationSeconds + tolerance &&
                    effect.EndSeconds > effect.StartSeconds;
                effectsSafe &= supported && finite && inside &&
                    !string.IsNullOrWhiteSpace(effect.Anchor);
                if (effect.EffectType == "RgbSplit")
                    rgbSplitTimes.Add(segment.OutputStartSeconds +
                        effect.StartSeconds);
            }
        }
        bool rgbSpacing = rgbSplitTimes
            .OrderBy(value => value)
            .Zip(rgbSplitTimes.OrderBy(value => value).Skip(1))
            .All(pair => pair.Second - pair.First >= 8 - tolerance);
        checks["effects_supported_and_bounded"] = effectsSafe;
        checks["rgb_split_spacing"] = rgbSpacing;
        AddViolation(violations, effectsSafe, "CINEMATIC_EFFECT_DIRECTIVE_INVALID");
        AddViolation(violations, rgbSpacing, "RGB_SPLIT_REPEATED_TOO_CLOSE");

        bool hasCameraPending = segments
            .Where(value => value.HighlightId is null)
            .SelectMany(value => value.Camera.Warnings)
            .Any(value => value.Contains(
                "PREVIEW_PENDING",
                StringComparison.OrdinalIgnoreCase));
        checks["camera_preview_gate"] = !hasCameraPending;
        AddViolation(violations, !hasCameraPending, "CAMERA_PREVIEW_GATE_NOT_PASSED");

        return new CinematicContractValidationReport(
            ContractVersion,
            violations.Count == 0,
            checks,
            violations.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyList<string> ValidateAudio(
        double integratedLoudnessLufs,
        double loudnessRangeLu,
        double truePeakDb)
    {
        List<string> violations = [];
        if (!double.IsFinite(integratedLoudnessLufs) ||
            Math.Abs(integratedLoudnessLufs - TargetIntegratedLoudnessLufs) > 0.5)
            violations.Add("AUDIO_INTEGRATED_LOUDNESS_OUT_OF_RANGE");
        if (!double.IsFinite(loudnessRangeLu) ||
            loudnessRangeLu > TargetLoudnessRangeLu + 0.5)
            violations.Add("AUDIO_LOUDNESS_RANGE_OUT_OF_RANGE");
        if (!double.IsFinite(truePeakDb) ||
            truePeakDb > MaximumTruePeakDb + 0.05)
            violations.Add("AUDIO_TRUE_PEAK_EXCEEDED");
        return violations;
    }

    private static bool IsContractVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;
        string numeric = version.Split('-', 2)[0];
        return Version.TryParse(numeric, out Version? parsed) &&
            parsed >= new Version(ContractVersion);
    }

    private static void AddViolation(
        List<string> violations,
        bool valid,
        string code)
    {
        if (!valid)
            violations.Add(code);
    }
}
