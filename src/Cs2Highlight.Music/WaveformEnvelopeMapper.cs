namespace Cs2Highlight.Music;

public static class WaveformEnvelopeMapper
{
    public static RealWaveformEnvelopeArtifact MapExcerpt(
        MusicWaveformEnvelope? source,
        double excerptStartSeconds,
        double excerptEndSeconds)
    {
        if (!double.IsFinite(excerptStartSeconds) ||
            !double.IsFinite(excerptEndSeconds) ||
            excerptStartSeconds < 0 ||
            excerptEndSeconds <= excerptStartSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(excerptEndSeconds),
                "Waveform excerpt bounds must be finite and ordered.");
        }

        if (source is null ||
            source.SchemaVersion != "1.0" ||
            !string.Equals(source.ChannelLayout, "mono", StringComparison.Ordinal) ||
            source.SamplesPerSecond is < 100 or > 200 ||
            source.Peaks.Count == 0)
        {
            return Unavailable(
                excerptStartSeconds,
                excerptEndSeconds,
                "REAL_WAVEFORM_ENVELOPE_UNAVAILABLE");
        }

        MusicWaveformPeak[] peaks = source.Peaks
            .Where(value =>
                value.TimeSeconds >= excerptStartSeconds - 0.000001 &&
                value.TimeSeconds < excerptEndSeconds + 0.000001 &&
                double.IsFinite(value.Min) &&
                double.IsFinite(value.Max) &&
                value.Min is >= 0 and <= 1 &&
                value.Max is >= 0 and <= 1)
            .OrderBy(value => value.TimeSeconds)
            .Select(value => value with
            {
                TimeSeconds = Math.Clamp(
                    value.TimeSeconds - excerptStartSeconds,
                    0,
                    excerptEndSeconds - excerptStartSeconds)
            })
            .ToArray();
        if (peaks.Length == 0)
        {
            return Unavailable(
                excerptStartSeconds,
                excerptEndSeconds,
                "REAL_WAVEFORM_EXCERPT_HAS_NO_SAMPLES");
        }

        return new RealWaveformEnvelopeArtifact
        {
            Available = true,
            ExcerptStartSeconds = excerptStartSeconds,
            ExcerptEndSeconds = excerptEndSeconds,
            SamplesPerSecond = source.SamplesPerSecond,
            Peaks = peaks,
            Warnings = []
        };
    }

    private static RealWaveformEnvelopeArtifact Unavailable(
        double start,
        double end,
        string warning) =>
        new()
        {
            Available = false,
            ExcerptStartSeconds = start,
            ExcerptEndSeconds = end,
            SamplesPerSecond = 0,
            Peaks = [],
            Warnings = [warning]
        };
}
