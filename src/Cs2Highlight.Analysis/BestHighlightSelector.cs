namespace Cs2Highlight.Analysis;

public interface IBestHighlightSelector
{
    HighlightCandidate? SelectBest(IReadOnlyList<HighlightCandidate> candidates);
}

public sealed class BestHighlightSelector : IBestHighlightSelector
{
    public HighlightCandidate? SelectBest(IReadOnlyList<HighlightCandidate> candidates) =>
        candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.KillCount)
            .ThenByDescending(candidate => candidate.HeadshotCount)
            .ThenBy(candidate => candidate.EndTick - candidate.StartTick)
            .ThenBy(candidate => candidate.RoundNumber)
            .ThenBy(candidate => candidate.FirstKillTick)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault();
}
