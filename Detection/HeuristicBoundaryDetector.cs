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
    private readonly IBoundaryDetectionService _boundaryDetectionService;

    public HeuristicBoundaryDetector(
        PluginConfiguration config,
        ILogger logger,
        IChapterManager chapterManager,
        IBoundaryDetectionService boundaryDetectionService)
    {
        _config = config;
        _logger = logger;
        _chapterManager = chapterManager;
        _boundaryDetectionService = boundaryDetectionService;
    }

    public async Task<Boundaries> DetectAsync(
        Movie item,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        var ffmpegPath = Plugin.FindFfmpeg(_logger);

        var credits = await _boundaryDetectionService.DetectCreditsAsync(
            ffmpegPath,
            item.Path,
            cues,
            totalDuration,
            ct);

        var targetEpisode =
            TimeSpan.FromMinutes(
                _config.TargetEpisodeMinutes);

        var initialCandidates =
            _boundaryDetectionService.GenerateCandidates(
                _chapterManager,
                item,
                cues,
                totalDuration,
                targetEpisode,
                BoundaryDetectionService.BoundaryWindow,
                credits);

        var candidates = await _boundaryDetectionService.ValidateCandidatesAsync(
            ffmpegPath,
            item.Path,
            initialCandidates,
            _config.EpisodeReminderSeconds,
            ct);

        var minEpisodeLength = TimeSpan.FromSeconds(targetEpisode.TotalSeconds * 0.5);

        var candidateTimes = candidates.Select(x => x.Time);

        var filtered = _boundaryDetectionService.EnforceMinimumEpisodeLength(
            candidateTimes,
            minEpisodeLength,
            totalDuration,
            credits);

        var boundaries = filtered
            .OrderBy(x => x)
            .ToList();

        return new Boundaries(boundaries, credits);
    }
}
