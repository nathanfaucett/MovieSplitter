using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

/// <summary>
/// Runs multiple detectors and merges results, collapsing timestamps
/// within <see cref="MergeWindowSeconds"/> of each other into one.
/// </summary>
public class CompositeBoundaryDetector : IBoundaryDetector
{
    public string Name => "Composite";

    private const double MergeWindowSeconds = 15.0;

    private readonly IReadOnlyList<IBoundaryDetector> _detectors;
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    public CompositeBoundaryDetector(
        IEnumerable<IBoundaryDetector> detectors,
        PluginConfiguration config,
        ILogger logger)
    {
        _detectors = detectors.ToList();
        _config    = config;
        _logger    = logger;
    }

    public async Task<IReadOnlyList<TimeSpan>> DetectAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        var all = new List<TimeSpan>();

        foreach (var detector in _detectors)
        {
            ct.ThrowIfCancellationRequested();
            var results = await detector.DetectAsync(cues, totalDuration, ct);
            _logger.LogInformation("[Composite] {Name} found {N} boundaries",
                detector.Name, results.Count);
            all.AddRange(results);
        }

        return MergeClose(all);
    }

    private static IReadOnlyList<TimeSpan> MergeClose(IEnumerable<TimeSpan> raw)
    {
        var window = TimeSpan.FromSeconds(MergeWindowSeconds);
        var sorted = raw.OrderBy(t => t).ToList();
        var merged = new List<TimeSpan>();

        foreach (var t in sorted)
        {
            if (merged.Count == 0 || t - merged.Last() > window)
                merged.Add(t);
        }

        return merged;
    }
}
