using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.Logging;
using MovieSplitter;
using MovieSplitter.Detection;
using MovieSplitter.Subtitle;
using MovieSplitter.Splitting;

namespace MovieSplitter.Services;

public class SplitWorkflow : ISplitWorkflow
{
  private readonly ISubtitleLoader _subtitleLoader;
  private readonly IFfmpegSplitter _splitter;
  private readonly ILibraryScanService _libraryScanService;
  private readonly ISeriesMetadataService _seriesMetadataService;
  private readonly IChapterManager _chapterManager;
  private readonly ILibraryManager _library;
  private readonly ILogger<SplitWorkflow> _logger;

  public SplitWorkflow(
      ISubtitleLoader subtitleLoader,
      IFfmpegSplitter splitter,
      ILibraryScanService libraryScanService,
      ISeriesMetadataService seriesMetadataService,
      IChapterManager chapterManager,
      ILibraryManager library,
      ILogger<SplitWorkflow> logger)
  {
    _subtitleLoader = subtitleLoader;
    _splitter = splitter;
    _libraryScanService = libraryScanService;
    _seriesMetadataService = seriesMetadataService;
    _chapterManager = chapterManager;
    _library = library;
    _logger = logger;
  }

  public async Task<SplitItemResult> ExecuteAsync(
      Movie movie,
      string preferredLanguage,
      PluginConfiguration config,
      double targetEpisodeMinutes,
      CancellationToken ct = default)
  {
    var subtitle = movie.GetMediaStreams()
        .Where(x => x.Type == MediaStreamType.Subtitle && !string.IsNullOrWhiteSpace(x.Path))
        .OrderByDescending(x => x.Language == preferredLanguage)
        .ThenByDescending(x => x.Language == "eng")
        .FirstOrDefault();

    if (subtitle is null)
    {
      return new SplitItemResult(
          SplitResultStatus.NotFound,
          0,
          $"Item {movie.Id} does not have any subtitles ({preferredLanguage}).");
    }

    var srtContent = await _subtitleLoader.LoadSubtitleTextAsync(subtitle, ct);
    if (srtContent is null)
    {
      return new SplitItemResult(
          SplitResultStatus.BadRequest,
          0,
          "No subtitles found for this movie.");
    }

    var cues = SrtParser.Parse(srtContent);
    var totalDuration = movie.RunTimeTicks.HasValue
        ? TimeSpan.FromTicks(movie.RunTimeTicks.Value)
        : TimeSpan.Zero;

    _logger.LogInformation("Creating boundary detector for {Mode}", config.DetectorMode);
    var detector = BoundaryDetectorFactory.Create(config, _logger, _chapterManager);
    _logger.LogInformation("Starting boundary detector {Name}", detector.Name);

    var boundaries = await detector.DetectAsync(movie, cues, totalDuration, ct);
    if (boundaries.StartTimes.Count == 0)
    {
      return new SplitItemResult(
          SplitResultStatus.Success,
          0,
          "No episode start times detected.");
    }

    var outputDirs = _library.GetVirtualFolders()
        .Where(x => x.CollectionType == CollectionTypeOptions.tvshows)
        .Select(x => x.Locations)
        .FirstOrDefault();

    if (outputDirs is null || outputDirs.Length == 0)
    {
      return new SplitItemResult(
          SplitResultStatus.NotFound,
          0,
          "No tv show folder found.");
    }

    _logger.LogInformation("Output locations {Locations}", string.Join(", ", outputDirs));

    var splitter = _splitter;
    var segments = await splitter.SplitAsync(
        movie.Path,
        boundaries.StartTimes,
        boundaries.Credits,
        totalDuration,
        outputDirs,
        movie.Name,
        ct);

    _logger.LogInformation("[Metadata] queuing library scan");
    await _libraryScanService.QueueScanAndWaitAsync(ct);

    await _seriesMetadataService.PatchSeriesMetadataAsync(movie, outputDirs, ct);

    return new SplitItemResult(
        SplitResultStatus.Success,
        segments.Count,
        null);
  }
}
