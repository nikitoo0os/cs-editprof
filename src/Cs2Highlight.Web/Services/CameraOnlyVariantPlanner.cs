using Cs2Highlight.Music;

namespace Cs2Highlight.Web.Services;

public static class CameraOnlyVariantPlanner
{
    public static CinematicMoviePlan Create(
        CinematicMoviePlan source,
        IReadOnlyList<CinematicSequenceSegment> cameraSegments)
    {
        if (cameraSegments.Count == 0)
            throw new ArgumentException(
                "At least one camera segment is required.",
                nameof(cameraSegments));
        if (cameraSegments.Any(value =>
                value.Camera.Family == CameraShotFamily.PlayerPov))
        {
            throw new ArgumentException(
                "Camera-only variants must not contain POV segments.",
                nameof(cameraSegments));
        }

        double cursor = 0;
        CinematicSequenceSegment[] rebased = cameraSegments
            .OrderBy(value => value.OutputStartSeconds)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(value =>
            {
                double duration = Math.Max(
                    1d / 60,
                    value.OutputEndSeconds - value.OutputStartSeconds);
                CinematicSequenceSegment segment = value with
                {
                    OutputStartSeconds = cursor,
                    OutputEndSeconds = cursor + duration,
                    Effects = []
                };
                cursor += duration;
                return segment;
            })
            .ToArray();

        return source with
        {
            SchemaVersion = "camera-only-1.0",
            TargetDurationSeconds = cursor,
            Segments = rebased,
            HighlightMatches = [],
            Warnings = source.Warnings.Concat(
            [
                "CAMERA_ONLY_VARIANT",
                "POV_STRICTLY_EXCLUDED"
            ]).Distinct(StringComparer.Ordinal).ToArray(),
            EffectRarity = null,
            CameraDiversity = ShotDiversityPolicy.AnalyzeFilm(
                rebased.Select(value => value.Camera).ToArray(),
                cursor)
        };
    }
}
