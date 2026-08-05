namespace Cs2Highlight.Analysis;

public interface IHighlightDetector
{
    IReadOnlyList<HighlightCandidate> Detect(
        DemoAnalysis analysis,
        HighlightDetectionOptions options);
}

public sealed class RuleBasedHighlightDetector : IHighlightDetector
{
    private readonly IWeaponCatalog weapons;

    public RuleBasedHighlightDetector() : this(new WeaponCatalog()) { }

    public RuleBasedHighlightDetector(IWeaponCatalog weapons) =>
        this.weapons = weapons;

    public IReadOnlyList<HighlightCandidate> Detect(
        DemoAnalysis analysis,
        HighlightDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(options);
        if (analysis.Demo.TickRate <= 0)
            throw new ArgumentException("Demo tick rate must be positive.", nameof(analysis));

        long maximumGap = SecondsToTicks(
            options.MaximumGapBetweenKillsSeconds, analysis.Demo.TickRate);
        long maximumDuration = SecondsToTicks(
            options.MaximumSequenceDurationSeconds, analysis.Demo.TickRate);
        Dictionary<int, DemoRound> rounds = analysis.Rounds
            .GroupBy(round => round.RoundNumber)
            .ToDictionary(group => group.Key, group => group.First());
        List<HighlightCandidate> multikills = [];
        List<HighlightCandidate> solos = [];

        IEnumerable<IGrouping<(int Round, string Player), KillEvent>> groups = analysis.Kills
            .Where(IsEligibleKill)
            .Where(kill =>
                options.TargetPlayerId is null ||
                string.Equals(kill.KillerPlayerId, options.TargetPlayerId, StringComparison.Ordinal))
            .GroupBy(kill => (kill.RoundNumber, kill.KillerPlayerId!));
        foreach (IGrouping<(int Round, string Player), KillEvent> group in groups)
        {
            KillEvent[] ordered = group
                .OrderBy(kill => kill.Tick)
                .ThenBy(kill => kill.EventIndex)
                .ToArray();
            HashSet<int> usedByMultikill = [];
            foreach (IReadOnlyList<KillEvent> sequence in BuildMaximalSequences(
                         ordered, maximumGap, maximumDuration, options.MinimumKills))
            {
                rounds.TryGetValue(group.Key.Round, out DemoRound? round);
                multikills.Add(BuildCandidate(analysis, sequence, round, options));
                usedByMultikill.UnionWith(sequence.Select(kill => kill.EventIndex));
            }

            List<HighlightCandidate> playerSolos = [];
            foreach (KillEvent kill in ordered.Where(kill =>
                         !usedByMultikill.Contains(kill.EventIndex)))
            {
                rounds.TryGetValue(kill.RoundNumber, out DemoRound? round);
                HighlightCandidate candidate = BuildCandidate(analysis, [kill], round, options);
                if (options.SoloKills.IncludeAllSoloKills ||
                    candidate.BeautyScore >= options.SoloKills.MinimumBeautyScore ||
                    HasSignificantSoloTag(candidate.Tags))
                    playerSolos.Add(candidate);
            }
            solos.AddRange(playerSolos
                .OrderByDescending(value => value.BeautyScore)
                .ThenBy(value => value.FirstKillTick)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .Take(Math.Max(0, options.SoloKills.MaximumSoloCandidatesPerDemo)));
        }

        return multikills.Concat(solos)
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

    private static bool HasSignificantSoloTag(IReadOnlyList<string> tags) =>
        tags.Any(tag => tag is "HEADSHOT" or "WALLBANG" or "ONE_TAP" or "KNIFE"
            or "ZEUS" or "NO_SCOPE" or "THROUGH_SMOKE" or "LOW_HP"
            or "LONG_DISTANCE" or "ROUND_ENDING_KILL");

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
                if (current.Count >= minimumKills) yield return current.ToArray();
                current = [];
            }
            current.Add(kill);
        }
        if (current.Count >= minimumKills) yield return current.ToArray();
    }

    private HighlightCandidate BuildCandidate(
        DemoAnalysis analysis,
        IReadOnlyList<KillEvent> sequence,
        DemoRound? round,
        HighlightDetectionOptions options)
    {
        KillEvent first = sequence[0];
        KillEvent last = sequence[^1];
        IReadOnlyList<KillDescriptor> descriptors = sequence
            .Select(ToDescriptor)
            .ToArray();
        IReadOnlyList<WeaponSequenceSegment> weaponSequence =
            WeaponCatalog.BuildSequence(descriptors, weapons);
        int headshots = sequence.Count(kill => kill.Headshot);
        HighlightType type = sequence.Count switch
        {
            >= 5 => HighlightType.Ace,
            4 => HighlightType.QuadKill,
            3 => HighlightType.TripleKill,
            2 => HighlightType.DoubleKill,
            _ => HighlightType.SoloKill
        };
        long startTick = Math.Max(
            0,
            first.Tick - SecondsToTicks(options.PreRollSeconds, analysis.Demo.TickRate));
        bool roundEnding = descriptors.Any(value => value.RoundEndingKill) ||
            (round is not null && last.Tick >= round.EndTick - analysis.Demo.TickRate);
        bool clampStart = options.ClampStartToRoundBounds || options.ClampToRoundBounds;
        if (clampStart && round is not null)
            startTick = Math.Max(startTick, round.StartTick);
        SafeClipTimingOptions safeOptions = options.SafeTiming;
        double minimumDuration = Math.Max(
            safeOptions.MinimumClipDurationSeconds,
            options.MinimumClipDurationSeconds);
        if (minimumDuration != safeOptions.MinimumClipDurationSeconds)
        {
            safeOptions = new SafeClipTimingOptions
            {
                SoloPostKillHoldSeconds = safeOptions.SoloPostKillHoldSeconds,
                MultikillPostKillHoldSeconds = safeOptions.MultikillPostKillHoldSeconds,
                RoundEndingPostKillHoldSeconds = Math.Max(
                    safeOptions.RoundEndingPostKillHoldSeconds,
                    options.RoundEndHoldSeconds),
                MinimumClipDurationSeconds = minimumDuration,
                DeathAnimationAllowanceSeconds = safeOptions.DeathAnimationAllowanceSeconds,
                KillfeedAllowanceSeconds = safeOptions.KillfeedAllowanceSeconds,
                AudioTailAllowanceSeconds = safeOptions.AudioTailAllowanceSeconds
            };
        }
        (SafeClipBounds safeBounds, long safeEndTick, long endTick) =
            SafeClipBoundsCalculator.Calculate(
                new SafeClipTimingRequest(
                    type,
                    startTick,
                    last.Tick,
                    last.Tick,
                    round?.EndTick,
                    roundEnding,
                    analysis.Demo.DurationTicks,
                    analysis.Demo.TickRate,
                    options.PostRollSeconds),
                safeOptions,
                options.MaximumClipDurationSeconds);
        if (endTick <= startTick)
            endTick = Math.Min(analysis.Demo.DurationTicks, startTick + 1);

        ScoreBreakdown score = Score(
            descriptors, round, first.KillerTeam, type, headshots,
            analysis.Demo.TickRate, options, weaponSequence);
        IReadOnlyList<string> tags = BuildTags(
            descriptors, round, first, last, headshots, analysis.Demo.TickRate, options, weaponSequence);
        string playerName = first.KillerName ??
            analysis.Players.FirstOrDefault(player => player.PlayerId == first.KillerPlayerId)?.Name ??
            first.KillerPlayerId!;
        string id =
            $"round-{first.RoundNumber}-player-{first.KillerPlayerId}-{first.Tick}-{last.Tick}";
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
            tags)
        {
            SourceDemoId = analysis.Demo.FileName,
            MapName = analysis.Demo.MapName,
            CombatScore = score.CombatScore,
            BeautyScore = score.BeautyScore,
            Kills = descriptors,
            WeaponSequence = weaponSequence,
            TickRate = analysis.Demo.TickRate,
            RoundStartTick = round?.StartTick,
            PrimaryKillTick = last.Tick,
            SafeEndTick = safeEndTick,
            SafeBounds = safeBounds,
            EstimatedDurationMilliseconds = (long)Math.Round(
                TicksToSeconds(endTick - startTick, analysis.Demo.TickRate) * 1000,
                MidpointRounding.AwayFromZero)
        };
    }

    private KillDescriptor ToDescriptor(KillEvent kill)
    {
        WeaponMetadata weapon = weapons.Resolve(kill.Weapon);
        return new KillDescriptor(
            kill.EventIndex,
            kill.Tick,
            kill.KillerPlayerId!,
            kill.VictimPlayerId,
            weapon.Code,
            kill.Headshot)
        {
            Wallbang = kill.Wallbang,
            OneTap = kill.OneTap,
            NoScope = kill.NoScope,
            ThroughSmoke = kill.ThroughSmoke,
            RoundEndingKill = kill.RoundEndingKill == true,
            LastEnemyKill = kill.LastEnemyKill == true,
            KillerHealth = kill.KillerHealth,
            DistanceMeters = kill.DistanceMeters,
            ShotsSinceLastKill = kill.ShotsSinceLastKill,
            ShooterPosition = kill.ShooterPosition,
            VictimPosition = kill.VictimPosition,
            HitPosition = kill.HitPosition
        };
    }

    private static ScoreBreakdown Score(
        IReadOnlyList<KillDescriptor> kills,
        DemoRound? round,
        string? killerTeam,
        HighlightType type,
        int headshots,
        int tickRate,
        HighlightDetectionOptions options,
        IReadOnlyList<WeaponSequenceSegment> weaponSequence)
    {
        HighlightScoringOptions combat = options.Scoring;
        BeautyScoringOptions beauty = options.BeautyScoring;
        double baseCombat = kills.Count * combat.KillWeight;
        double typeBonus = type switch
        {
            HighlightType.TripleKill => combat.TripleKillBonus,
            HighlightType.QuadKill => combat.QuadKillBonus,
            HighlightType.Ace => combat.AceBonus,
            _ => 0
        };
        double fastBonus = kills.Count > 1 &&
            TicksToSeconds(kills[^1].Tick - kills[0].Tick, tickRate) <= combat.FastSequenceSeconds
                ? combat.FastSequenceBonus
                : 0;
        double roundWinBonus = round is not null &&
            killerTeam is not null &&
            string.Equals(round.Winner, killerTeam, StringComparison.OrdinalIgnoreCase)
                ? combat.RoundWinBonus
                : 0;
        double endBonus = kills.Any(value => value.RoundEndingKill)
            ? combat.LastKillRoundEndBonus
            : 0;
        double beautyBase = kills.Count * beauty.BaseKillScore;
        double headshotBonus = headshots * beauty.HeadshotBonus;
        double wallbang = kills.Count(value => value.Wallbang == true) * beauty.WallbangBonus;
        double oneTap = kills.Count(value => value.OneTap == true) * beauty.OneTapBonus;
        double knife = kills.Count(value => value.WeaponCode == "knife") * beauty.KnifeBonus;
        double zeus = kills.Count(value => value.WeaponCode == "taser") * beauty.ZeusBonus;
        double noScope = kills.Count(value => value.NoScope == true) * beauty.NoScopeBonus;
        double smoke = kills.Count(value => value.ThroughSmoke == true) * beauty.ThroughSmokeBonus;
        double roundEnding = kills.Count(value => value.RoundEndingKill) * beauty.RoundEndingBonus;
        double lastEnemy = kills.Count(value => value.LastEnemyKill) * beauty.LastEnemyBonus;
        double lowHealth = kills.Count(value =>
            value.KillerHealth is not null &&
            value.KillerHealth <= beauty.LowHealthThreshold) * beauty.LowHealthBonus;
        double distance = kills.Count(value =>
            value.DistanceMeters is not null &&
            value.DistanceMeters >= beauty.LongDistanceThresholdMeters) * beauty.LongDistanceBonus;
        double swap = Math.Max(0, weaponSequence.Count - 1) * beauty.WeaponSwapBonus;
        double combatScore = baseCombat + typeBonus + fastBonus + roundWinBonus + endBonus;
        double beautyScore = beautyBase + headshotBonus + wallbang + oneTap + knife + zeus +
            noScope + smoke + roundEnding + lastEnemy + lowHealth + distance + swap;
        return new ScoreBreakdown(
            baseCombat, headshotBonus, typeBonus, fastBonus, roundWinBonus, endBonus,
            combatScore + beautyScore)
        {
            BeautyBaseScore = beautyBase,
            WallbangBonus = wallbang,
            OneTapBonus = oneTap,
            KnifeBonus = knife,
            ZeusBonus = zeus,
            NoScopeBonus = noScope,
            ThroughSmokeBonus = smoke,
            LowHealthBonus = lowHealth,
            LongDistanceBonus = distance,
            LastEnemyBonus = lastEnemy,
            WeaponSwapBonus = swap,
            CombatScore = combatScore,
            BeautyScore = beautyScore
        };
    }

    private static string[] BuildTags(
        IReadOnlyList<KillDescriptor> kills,
        DemoRound? round,
        KillEvent first,
        KillEvent last,
        int headshots,
        int tickRate,
        HighlightDetectionOptions options,
        IReadOnlyList<WeaponSequenceSegment> weaponSequence)
    {
        HashSet<string> tags = new(StringComparer.Ordinal);
        if (headshots > 0) tags.Add("HEADSHOT");
        if (headshots >= options.MinimumHeadshotsForStreak) tags.Add("HEADSHOT_STREAK");
        if (kills.Any(value => value.Wallbang == true)) tags.Add("WALLBANG");
        if (kills.Any(value => value.OneTap == true)) tags.Add("ONE_TAP");
        if (kills.Any(value => value.NoScope == true)) tags.Add("NO_SCOPE");
        if (kills.Any(value => value.ThroughSmoke == true)) tags.Add("THROUGH_SMOKE");
        if (kills.Any(value => value.RoundEndingKill)) tags.Add("ROUND_ENDING_KILL");
        if (kills.Any(value => value.LastEnemyKill)) tags.Add("LAST_ENEMY");
        if (kills.Any(value => value.WeaponCode == "knife")) tags.Add("KNIFE");
        if (kills.Any(value => value.WeaponCode == "taser")) tags.Add("ZEUS");
        if (kills.Any(value =>
                value.KillerHealth is not null &&
                value.KillerHealth <= options.BeautyScoring.LowHealthThreshold))
            tags.Add("LOW_HP");
        if (kills.Any(value =>
                value.DistanceMeters is not null &&
                value.DistanceMeters >= options.BeautyScoring.LongDistanceThresholdMeters))
            tags.Add("LONG_DISTANCE");
        if (weaponSequence.Count > 1) tags.Add("WEAPON_SWAP");
        if (kills.Count > 1 &&
            TicksToSeconds(last.Tick - first.Tick, tickRate) <= options.Scoring.FastSequenceSeconds)
            tags.Add("FAST_SEQUENCE");
        if (round is not null &&
            first.KillerTeam is not null &&
            string.Equals(first.KillerTeam, round.Winner, StringComparison.OrdinalIgnoreCase))
            tags.Add("ROUND_WIN");
        return tags.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static long SecondsToTicks(double seconds, int tickRate) =>
        checked((long)Math.Round(seconds * tickRate, MidpointRounding.AwayFromZero));

    private static double TicksToSeconds(long ticks, int tickRate) =>
        (double)ticks / tickRate;
}
