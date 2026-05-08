using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

public class HeuristicBoundaryDetector : IBoundaryDetector
{
    public string Name => "Heuristic";

    private static readonly TimeSpan BoundaryWindow =
        TimeSpan.FromMinutes(10);

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    public HeuristicBoundaryDetector(
        PluginConfiguration config,
        ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task<IReadOnlyList<TimeSpan>> DetectAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        var candidates = FindCandidates(cues);

        var filtered = BalanceEpisodes(
            candidates,
            totalDuration);

        return Task.FromResult<IReadOnlyList<TimeSpan>>(filtered);
    }

    private List<CandidateBoundary> FindCandidates(
        IReadOnlyList<SubtitleCue> cues)
    {
        var results = new List<CandidateBoundary>();

        var silenceThreshold =
            TimeSpan.FromSeconds(_config.SilenceThresholdSeconds);

        for (int i = 1; i < cues.Count; i++)
        {
            var gap = cues[i].Start - cues[i - 1].End;

            if (gap < silenceThreshold)
                continue;

            var midpoint = cues[i - 1].End + gap / 2;

            results.Add(new CandidateBoundary
            {
                Time = midpoint,
                SilenceGap = gap
            });

            _logger.LogDebug(
                "[Heuristic] Silence gap {Gap:g} -> candidate at {Time:g}",
                gap,
                midpoint);
        }

        return results
            .OrderBy(x => x.Time)
            .ToList();
    }

    private IReadOnlyList<TimeSpan> BalanceEpisodes(
        List<CandidateBoundary> candidates,
        TimeSpan totalDuration)
    {
        if (candidates.Count == 0)
            return [];

        var targetEpisode =
            TimeSpan.FromMinutes(_config.TargetEpisodeMinutes);

        var result = new List<TimeSpan>();

        var currentStart = TimeSpan.Zero;

        while (true)
        {
            var targetBoundary = currentStart + targetEpisode;

            var nearby = candidates
                .Where(c =>
                    c.Time > currentStart &&
                    c.Time >= targetBoundary - BoundaryWindow &&
                    c.Time <= targetBoundary + BoundaryWindow)
                .ToList();

            if (nearby.Count == 0)
            {
                nearby = candidates
                    .Where(c => c.Time > currentStart)
                    .Take(5)
                    .ToList();
            }

            if (nearby.Count == 0)
                break;

            var best = nearby
                .OrderBy(c =>
                    Math.Abs((c.Time - targetBoundary).TotalSeconds))
                .ThenByDescending(c => c.SilenceGap)
                .First();

            // Don't create a tiny tail episode
            if (totalDuration - best.Time < targetEpisode / 2)
                break;

            result.Add(best.Time);

            _logger.LogDebug(
                "[Heuristic] Selected boundary at {Time:g}",
                best.Time);

            currentStart = best.Time;
        }

        return result;
    }

    private sealed class CandidateBoundary
    {
        public required TimeSpan Time { get; init; }

        public required TimeSpan SilenceGap { get; init; }
    }
}
