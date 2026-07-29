namespace Cs2Highlight.Analysis;

public static class AnalysisValidator
{
    private static readonly HashSet<string> SupportedSchemaVersions =
        new(StringComparer.Ordinal) { "1.0", "1.1", "1.2" };

    public static DemoAnalysis Validate(DemoAnalysis analysis)
    {
        if (!SupportedSchemaVersions.Contains(analysis.SchemaVersion))
        {
            throw Error(
                "UNSUPPORTED_ANALYSIS_SCHEMA",
                $"Expected demo-analysis schema 1.0, 1.1 or 1.2, got {analysis.SchemaVersion}.");
        }
        if (analysis.Demo.TickRate <= 0)
        {
            throw Error("INVALID_TICK_RATE", "Analysis tickRate must be positive.");
        }
        if (analysis.Demo.DurationTicks <= 0)
        {
            throw Error("INVALID_ANALYSIS_JSON", "Analysis durationTicks must be positive.");
        }
        if (analysis.Rounds.Any(round =>
                round.RoundNumber <= 0 ||
                round.StartTick < 0 ||
                round.EndTick <= round.StartTick ||
                round.EndTick > analysis.Demo.DurationTicks))
        {
            throw Error("INVALID_ROUND_BOUNDS", "Analysis contains invalid round boundaries.");
        }
        if (analysis.Kills.Any(kill =>
                kill.EventIndex <= 0 ||
                kill.RoundNumber <= 0 ||
                kill.Tick < 0 ||
                kill.Tick > analysis.Demo.DurationTicks ||
                string.IsNullOrWhiteSpace(kill.VictimPlayerId)))
        {
            throw Error("INVALID_ANALYSIS_JSON", "Analysis contains an invalid kill event.");
        }
        if (analysis.Kills.Select(kill => kill.EventIndex).Distinct().Count() != analysis.Kills.Count)
        {
            throw Error("INVALID_ANALYSIS_JSON", "Analysis contains duplicate event indexes.");
        }
        if (analysis.Timeline.Any(frame =>
                frame.Tick < 0 ||
                frame.Tick > analysis.Demo.DurationTicks ||
                frame.RoundNumber <= 0 ||
                frame.MovementSpeed < 0 ||
                frame.ActionDensity is < 0 or > 1 ||
                string.IsNullOrWhiteSpace(frame.Player.PlayerId)))
        {
            throw Error(
                "INVALID_GAMEPLAY_TIMELINE",
                "Analysis contains an invalid gameplay timeline frame.");
        }
        return analysis;
    }

    private static AnalysisException Error(string code, string message) =>
        new(new AnalysisError(code, message, AnalysisStage.ValidatingAnalysis, false));
}
