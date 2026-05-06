using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using MovieSplitter.Detection;
using MovieSplitter.Splitting;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Tasks;

public class SplitMovieTask : IScheduledTask
{
    public string Name => "Split Movies into Episodes";
    public string Key => "MovieSplitter_Split";
    public string Description => "Parses subtitles to detect episode boundaries and splits the file using the configured detector (heuristic, Ollama, or composite).";
    public string Category => "Movie Splitter";

    private readonly ILibraryManager _library;
    private readonly ILogger<SplitMovieTask> _logger;

    public SplitMovieTask(
        ILibraryManager library,
        ILogger<SplitMovieTask> logger)
    {
        _library = library;
        _logger = logger;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        Array.Empty<TaskTriggerInfo>(); // manual trigger only by default

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var movies = _library.GetItemList(new()
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false
        }).OfType<Movie>().ToList();

        for (int i = 0; i < movies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / movies.Count);
            await ProcessMovieAsync(movies[i], ct);
        }

        progress.Report(100);
    }

    internal async Task ProcessMovieAsync(Movie movie, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        _logger.LogInformation("Processing: {Movie}", movie.Name);

        var loader = new SubtitleLoader(config, _logger);
        var srtContent = await loader.LoadAsync(movie.Path, ct);
        if (srtContent is null)
        {
            _logger.LogWarning("No subtitles found for {Movie}, skipping.", movie.Name);
            return;
        }

        var cues = SrtParser.Parse(srtContent);
        var totalDuration = movie.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(movie.RunTimeTicks.Value) : TimeSpan.Zero;

        _logger.LogInformation("Loaded {N} subtitle cues", cues.Count);

        var detector = BoundaryDetectorFactory.Create(config, _logger);
        var boundaries = await detector.DetectAsync(cues, totalDuration, ct);

        _logger.LogInformation("Found {N} boundaries → {Ep} episodes",
            boundaries.Count, boundaries.Count + 1);

        if (boundaries.Count == 0)
        {
            _logger.LogInformation("No split points detected for {Movie}.", movie.Name);
            return;
        }

        var outputDir = Path.Combine(Path.GetDirectoryName(movie.Path)!, config.OutputSubfolder);
        var ffmpegPath = Plugin.Instance!.GetFfmpegPath();
        var splitter = new FfmpegSplitter(ffmpegPath, _logger);
        var segments = await splitter.SplitAsync(
            movie.Path, boundaries, totalDuration, outputDir, movie.Name, ct);

        _library.QueueLibraryScan();
        _logger.LogInformation("Done. Created {N} episode files.", segments.Count);
    }
}
