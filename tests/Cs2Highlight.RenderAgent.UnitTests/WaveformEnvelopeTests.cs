using Cs2Highlight.Music;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class WaveformEnvelopeTests
{
    [Fact]
    public void MapsOnlyRealSamplesFromSelectedExcerpt()
    {
        MusicWaveformEnvelope source = new()
        {
            SamplesPerSecond = 100,
            SourceEndSeconds = 4,
            Peaks =
            [
                new MusicWaveformPeak(0, 0.1, 0.2),
                new MusicWaveformPeak(1, 0.3, 0.4),
                new MusicWaveformPeak(2, 0.5, 0.6),
                new MusicWaveformPeak(3, 0.7, 0.8)
            ]
        };

        RealWaveformEnvelopeArtifact result =
            WaveformEnvelopeMapper.MapExcerpt(source, 1, 3);

        Assert.True(result.Available);
        Assert.Equal(3, result.Peaks.Count);
        Assert.Equal(0, result.Peaks[0].TimeSeconds);
        Assert.Equal(2, result.Peaks[^1].TimeSeconds);
        Assert.DoesNotContain(result.Peaks, value => value.Min == 0.1);
    }

    [Fact]
    public void MissingLegacyEnvelopeIsExplicitlyUnavailable()
    {
        RealWaveformEnvelopeArtifact result =
            WaveformEnvelopeMapper.MapExcerpt(null, 5, 12);

        Assert.False(result.Available);
        Assert.Empty(result.Peaks);
        Assert.Contains(
            "REAL_WAVEFORM_ENVELOPE_UNAVAILABLE",
            result.Warnings);
    }
}
