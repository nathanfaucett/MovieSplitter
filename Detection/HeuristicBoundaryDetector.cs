using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

public class HeuristicBoundaryDetector : IBoundaryDetector
{
    public string Name => "Heuristic";

    private readonly PluginConfiguration _config;
    private readonly CueWordMatcher _cueMatcher;
    private readonly ILogger _logger;

    public HeuristicBoundaryDetector(PluginConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _cueMatcher = new CueWordMatcher(config.CueWordPatterns);
    }

    public Task<IReadOnlyList<TimeSpan>> DetectAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        var candidates = new HashSet<TimeSpan>();

        // Cue-word matches
        foreach (var ts in _cueMatcher.FindCueBoundaries(cues))
        {
            _logger.LogDebug("[Heuristic] Cue-word boundary at {T}", ts);
            candidates.Add(ts);
        }

        // Long silence gaps between subtitle blocks
        var silence = TimeSpan.FromSeconds(_config.SilenceThresholdSeconds);
        for (int i = 1; i < cues.Count; i++)
        {
            var gap = cues[i].Start - cues[i - 1].End;
            if (gap >= silence)
            {
                var mid = cues[i - 1].End + gap / 2;
                _logger.LogDebug("[Heuristic] Silence gap {G:g} → boundary at {T}", gap, mid);
                candidates.Add(mid);
            }
        }

        return Task.FromResult(Filter(candidates, totalDuration));
    }

    private IReadOnlyList<TimeSpan> Filter(
        HashSet<TimeSpan> candidates, TimeSpan totalDuration)
    {
        var min    = TimeSpan.FromMinutes(_config.MinEpisodeMinutes);
        var sorted = candidates.OrderBy(t => t).ToList();
        var result = new List<TimeSpan>();
        var last   = TimeSpan.Zero;

        foreach (var t in sorted)
        {
            if (t - last >= min) { result.Add(t); last = t; }
        }

        if (result.Count > 0 && totalDuration - result.Last() < min)
            result.RemoveAt(result.Count - 1);

        return result;
    }
}
