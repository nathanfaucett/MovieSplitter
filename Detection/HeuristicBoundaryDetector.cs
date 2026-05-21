using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

public class HeuristicBoundaryDetector : IBoundaryDetector
{
    public string Name => "Heuristic";

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private readonly IChapterManager _chapterManager;

    public HeuristicBoundaryDetector(
        PluginConfiguration config,
        ILogger logger,
        IChapterManager chapterManager)
    {
        _config = config;
        _logger = logger;
        _chapterManager = chapterManager;
    }

    public async Task<Boundaries> DetectAsync(
        Movie item,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        var ffmpegPath = Plugin.FindFfmpeg(_logger);

        var credits = await BoundaryDetectionHelper.DetectCreditsAsync(
            _logger,
            ffmpegPath,
            item.Path,
            cues,
            totalDuration,
            ct);

        var targetEpisode =
            TimeSpan.FromMinutes(
                _config.TargetEpisodeMinutes);

        var candidates =
            BoundaryDetectionHelper.GenerateCandidates(
                _logger,
                _chapterManager,
                item,
                cues,
                totalDuration,
                targetEpisode,
                BoundaryDetectionHelper.BoundaryWindow,
                credits);

        var boundaries = candidates
            .Select(x => x.Time)
            .OrderBy(x => x)
            .ToList();

        return new Boundaries(boundaries, credits);
    }
}
