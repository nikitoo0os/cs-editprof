namespace Cs2Highlight.Analysis;

public interface IHighlightDetector
{
    IReadOnlyList<HighlightCandidate> Detect(
        DemoAnalysis analysis,
        HighlightDetectionOptions options);
}

public sealed class RuleBasedHighlightDetector : IHighlightDetector
{
    public IReadOnlyList<HighlightCandidate> Detect(
        DemoAnalysis analysis,
        HighlightDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(options);
        if (analysis.Demo.TickRate <= 0)
        {
            throw new ArgumentException("Demo tick rate must be positive.", nameof(analysis));
        }

        long maximumGap = SecondsToTicks(options.MaximumGapBetweenKillsSeconds, analysis.Demo.TickRate);
        long maximumDuration = SecondsToTicks(options.MaximumSequenceDurationSeconds, analysis.Demo.TickRate);
        Dictionary<int, DemoRound> rounds = analysis.Rounds
            .GroupBy(round => round.RoundNumber)
            .ToDictionary(group => group.Key, group => group.First());
        List<HighlightCandidate> candidates = [];

        IEnumerable<IGrouping<(int Round, string Player), KillEvent>> groups = analysis.Kills
            .Where(IsEligibleKill)
            .Where(kill =>
                options.TargetPlayerId is null ||
                string.Equals(
                    kill.KillerPlayerId,
                    options.TargetPlayerId,
                    StringComparison.Ordinal))
            .GroupBy(kill => (kill.RoundNumber, kill.KillerPlayerId!));
        foreach (IGrouping<(int Round, string Player), KillEvent> group in groups)
        {
            List<KillEvent> ordered = group
                .OrderBy(kill => kill.Tick)
                .ThenBy(kill => kill.EventIndex)
                .ToList();
            foreach (IReadOnlyList<KillEvent> sequence in BuildMaximalSequences(
                         ordered,
                         maximumGap,
                         maximumDuration,
                         options.MinimumKills))
            {
                rounds.TryGetValue(group.Key.Round, out DemoRound? round);
                candidates.Add(BuildCandidate(analysis, sequence, round, options));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.FirstKillTick)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsEligibleKill(KillEvent kill) =>
        kill.KillerPlayerId is not null &&
        !string.Equals(kill.KillerPlayerId, kill.VictimPlayerId, StringComparison.Ordinal) &&
        (kill.KillerTeam is null ||
         kill.VictimTeam is null ||
         !string.Equals(kill.KillerTeam, kill.VictimTeam, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<IReadOnlyList<KillEvent>> BuildMaximalSequences(
        IReadOnlyList<KillEvent> kills,
        long maximumGap,
        long maximumDuration,
        int minimumKills)
    {
        List<KillEvent> current = [];
        foreach (KillEvent kill in kills)
        {
            bool continues = current.Count == 0 ||
                (kill.Tick - current[^1].Tick <= maximumGap &&
                 kill.Tick - current[0].Tick <= maximumDuration);
            if (!continues)
            {
                if (current.Count >= minimumKills)
                {
                    yield return current.ToArray();
                }
                current = [];
            }
            current.Add(kill);
        }
        if (current.Count >= minimumKills)
        {
            yield return current.ToArray();
        }
    }

    private static HighlightCandidate BuildCandidate(
        DemoAnalysis analysis,
        IReadOnlyList<KillEvent> sequence,
        DemoRound? round,
        HighlightDetectionOptions options)
    {
        KillEvent first = sequence[0];
        KillEvent last = sequence[^1];
        int headshots = sequence.Count(kill => kill.Headshot);
        long startTick = Math.Max(0, first.Tick - SecondsToTicks(options.PreRollSeconds, analysis.Demo.TickRate));
        long endTick = Math.Min(
            analysis.Demo.DurationTicks,
            last.Tick + SecondsToTicks(options.PostRollSeconds, analysis.Demo.TickRate));
        if (options.ClampToRoundBounds && round is not null)
        {
            startTick = Math.Max(startTick, round.StartTick);
            endTick = Math.Min(endTick, round.EndTick);
        }
        if (endTick <= startTick)
        {
            endTick = Math.Min(analysis.Demo.DurationTicks, startTick + 1);
        }

        HighlightType type = sequence.Count switch
        {
            >= 5 => HighlightType.Ace,
            4 => HighlightType.QuadKill,
            3 => HighlightType.TripleKill,
            _ => HighlightType.DoubleKill
        };
        ScoreBreakdown score = Score(sequence, round, type, headshots, analysis.Demo.TickRate, options);
        List<string> tags = [];
        if (headshots >= options.MinimumHeadshotsForStreak)
        {
            tags.Add("HEADSHOT_STREAK");
        }
        if (TicksToSeconds(last.Tick - first.Tick, analysis.Demo.TickRate) <= options.Scoring.FastSequenceSeconds)
        {
            tags.Add("FAST_SEQUENCE");
        }
        if (round is not null &&
            first.KillerTeam is not null &&
            string.Equals(first.KillerTeam, round.Winner, StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("ROUND_WIN");
        }

        string playerName = first.KillerName ??
            analysis.Players.FirstOrDefault(player => player.PlayerId == first.KillerPlayerId)?.Name ??
            first.KillerPlayerId!;
        string id = $"round-{first.RoundNumber}-player-{first.KillerPlayerId}-{first.Tick}-{last.Tick}";
        return new HighlightCandidate(
            id,
            type,
            first.KillerPlayerId!,
            playerName,
            first.RoundNumber,
            first.Tick,
            last.Tick,
            startTick,
            endTick,
            sequence.Count,
            headshots,
            score.Total,
            score,
            sequence.Select(kill => kill.EventIndex).ToArray(),
            tags);
    }

    private static ScoreBreakdown Score(
        IReadOnlyList<KillEvent> sequence,
        DemoRound? round,
        HighlightType type,
        int headshots,
        int tickRate,
        HighlightDetectionOptions options)
    {
        HighlightScoringOptions scoring = options.Scoring;
        double baseScore = sequence.Count * scoring.KillWeight;
        double headshotBonus = headshots >= options.MinimumHeadshotsForStreak
            ? scoring.HeadshotStreakBonus + Math.Max(0, headshots - 1) * scoring.AdditionalHeadshotWeight
            : 0;
        double typeBonus = type switch
        {
            HighlightType.TripleKill => scoring.TripleKillBonus,
            HighlightType.QuadKill => scoring.QuadKillBonus,
            HighlightType.Ace => scoring.AceBonus,
            _ => 0
        };
        double fastBonus = TicksToSeconds(sequence[^1].Tick - sequence[0].Tick, tickRate) <=
            scoring.FastSequenceSeconds
            ? scoring.FastSequenceBonus
            : 0;
        double roundWinBonus = round is not null &&
            sequence[0].KillerTeam is not null &&
            string.Equals(sequence[0].KillerTeam, round.Winner, StringComparison.OrdinalIgnoreCase)
            ? scoring.RoundWinBonus
            : 0;
        double endBonus = round is not null && sequence[^1].Tick == round.EndTick
            ? scoring.LastKillRoundEndBonus
            : 0;
        double total = baseScore + headshotBonus + typeBonus + fastBonus + roundWinBonus + endBonus;
        return new ScoreBreakdown(baseScore, headshotBonus, typeBonus, fastBonus, roundWinBonus, endBonus, total);
    }

    private static long SecondsToTicks(double seconds, int tickRate) =>
        checked((long)Math.Round(seconds * tickRate, MidpointRounding.AwayFromZero));

    private static double TicksToSeconds(long ticks, int tickRate) => (double)ticks / tickRate;
}
