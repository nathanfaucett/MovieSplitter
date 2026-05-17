using System.Text;
using System.Xml.Linq;

using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MovieSplitter.Detection;
using MovieSplitter.Splitting;
using MovieSplitter.Subtitle;


namespace MovieSplitter.Api;

[ApiController]
[Route("MovieSplitter")]
public class MovieSplitterController : ControllerBase
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(30);

    // Image filenames Jellyfin/Kodi recognise at the series folder level
    private static readonly string[] SeriesImageNames =
    [
        "poster", "folder", "cover", "default",
        "fanart", "backdrop", "banner", "logo", "clearart", "disc", "landscape"
    ];

    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".webp", ".tbn"
    ];

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _library;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<MovieSplitterController> _logger;
    private readonly IChapterManager _chapterManager;

    public MovieSplitterController(
        IUserManager userManager,
        ILibraryManager library,
        ITaskManager taskManager,
        ILogger<MovieSplitterController> logger,
        IChapterManager chapterManager)
    {
        _userManager = userManager;
        _library = library;
        _taskManager = taskManager;
        _logger = logger;
        _chapterManager = chapterManager;
    }

    /// <summary>
    /// Split a single movie by its Jellyfin item ID.
    /// POST /MovieSplitter/SplitItem?itemId={guid}
    /// </summary>
    [HttpPost("SplitItem")]
    [Authorize]
    public async Task<ActionResult<SplitItemResult>> SplitItem(
        [FromQuery] Guid itemId,
        [FromQuery] double? targetEpisodeMinutes,
        CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is not Movie movie)
            return NotFound(new { Error = $"Item {itemId} is not a movie." });

        var config = Plugin.Instance!.Configuration;
        config.TargetEpisodeMinutes = targetEpisodeMinutes ?? config.TargetEpisodeMinutes;

        var preferredLanguageResult = GetLanguage();
        var preferredLanguage = "eng";
        if (preferredLanguageResult.Result is UnauthorizedResult)
            preferredLanguage = preferredLanguageResult.Value!;

        var subtitle = movie.GetMediaStreams()
            .Where(x => x.Type == MediaStreamType.Subtitle && !string.IsNullOrWhiteSpace(x.Path))
            .OrderByDescending(x => x.Language == preferredLanguage)
            .ThenByDescending(x => x.Language == "eng")
            .FirstOrDefault();

        if (subtitle is null)
            return NotFound(new { Error = $"Item {itemId} does not have any subtitles ({preferredLanguage})." });

        var srtContent = await GetSubtitleTextAsync(subtitle);
        if (srtContent is null)
            return BadRequest(new { Error = "No subtitles found for this movie." });

        var cues = SrtParser.Parse(srtContent);
        var totalDuration = movie.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(movie.RunTimeTicks.Value) : TimeSpan.Zero;

        _logger.LogInformation("Creating boundary detector for {Mode}", config.DetectorMode);
        var detector = BoundaryDetectorFactory.Create(config, _logger, _chapterManager);
        _logger.LogInformation("Starting boundary detector {Name}", detector.Name);
        var boundaries = await detector.DetectAsync(movie, cues, totalDuration, ct);

        if (boundaries.StartTimes.Count == 0)
            return Ok(new SplitItemResult(0, "No episode start times detected."));

        _logger.LogInformation(
            "Finished boundary detector boundaries={Count} credits={Credits}",
            boundaries.StartTimes.Count,
            boundaries.Credits?.Item1);

        var outputDirs = _library.GetVirtualFolders()
            .Where(x => x.CollectionType == CollectionTypeOptions.tvshows)
            .Select(x => x.Locations)
            .FirstOrDefault();

        if (outputDirs is null)
            return NotFound(new SplitItemResult(0, "No tv show folder found."));

        _logger.LogInformation("Output locations {Locations}", string.Join(", ", outputDirs));

        // ── 1. Split ──────────────────────────────────────────────────────────
        var ffmpegPath = Plugin.Instance!.GetFfmpegPath();
        var splitter = new FfmpegSplitter(ffmpegPath, _logger);
        _logger.LogInformation("Starting ffmpeg");
        var segments = await splitter.SplitAsync(
            movie.Path, boundaries.StartTimes, boundaries.Credits,
            totalDuration, outputDirs, movie.Name, ct);

        // ── 2. Trigger scan and wait for it to finish ─────────────────────────
        _logger.LogInformation("[Metadata] queuing library scan");
        await RunLibraryScanAsync(ct);

        // ── 3. Patch the NFO Jellyfin just wrote + copy images ────────────────
        await PatchSeriesMetadataAsync(movie, outputDirs, ct);

        return Ok(new SplitItemResult(segments.Count, null));
    }

    // ── Scan helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Queues a full library scan and waits for it to complete.
    /// Resolves as soon as the RefreshMediaLibraryTask transitions back to Idle.
    /// </summary>
    private async Task RunLibraryScanAsync(CancellationToken ct)
    {
        var scanTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t =>
                string.Equals(
                    t.Name,
                    "Scan Media Library",
                    StringComparison.OrdinalIgnoreCase));

        if (scanTask is null)
        {
            throw new InvalidOperationException(
                "Could not find Scan Media Library task");
        }

        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
        {
            if (e.Task.Id != scanTask.Id)
            {
                return;
            }

            _logger.LogInformation(
                "[Metadata] library scan completed with status {Status}",
                e.Result.Status);

            tcs.TrySetResult();
        }

        _taskManager.TaskCompleted += OnTaskCompleted;

        try
        {
            _logger.LogInformation(
                "[Metadata] queueing library scan task {TaskId}",
                scanTask.Id);

            // invoke QueueScheduledTask<T>() dynamically
            var method = typeof(ITaskManager)
                .GetMethods()
                .First(m =>
                    m.Name == "QueueScheduledTask" &&
                    m.IsGenericMethod);

            var generic = method.MakeGenericMethod(
                scanTask.ScheduledTask.GetType());

            generic.Invoke(
                _taskManager,
                [new TaskOptions()]);

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);

            timeoutCts.CancelAfter(ScanTimeout);

            await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[Metadata] library scan timed out after {Timeout}",
                ScanTimeout);
        }
        finally
        {
            _taskManager.TaskCompleted -= OnTaskCompleted;
        }
    }

    // ── Metadata helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// After the library scan, Jellyfin has written its own tvshow.nfo.
    /// We patch it with the source movie's identity fields, then copy images.
    /// </summary>
    private async Task PatchSeriesMetadataAsync(
        Movie movie,
        string[] outputDirs,
        CancellationToken ct)
    {
        var safeName = string.Concat(movie.Name.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim(' ', '.', '_', '-');

        var movieDir = Path.GetDirectoryName(movie.Path);

        // Read source movie.nfo once
        string? rawMovieNfo = null;
        if (movieDir is not null)
        {
            var stem = Path.GetFileNameWithoutExtension(movie.Path);
            var sourceNfo = new[]
            {
                Path.Combine(movieDir, stem + ".nfo"),
                Path.Combine(movieDir, "movie.nfo"),
            }.FirstOrDefault(System.IO.File.Exists);

            if (sourceNfo is not null)
            {
                try
                {
                    rawMovieNfo = await System.IO.File.ReadAllTextAsync(sourceNfo, ct);
                    _logger.LogInformation("[Metadata] read source NFO from {Src}", sourceNfo);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Metadata] failed to read source NFO");
                }
            }
            else
            {
                _logger.LogWarning("[Metadata] no movie.nfo found next to {Path}", movie.Path);
            }
        }

        foreach (var outputDir in outputDirs)
        {
            ct.ThrowIfCancellationRequested();

            var seriesDir = Path.Combine(outputDir, safeName);
            if (!Directory.Exists(seriesDir))
            {
                _logger.LogWarning("[Metadata] series dir not found: {Dir}", seriesDir);
                continue;
            }

            // ── Patch tvshow.nfo ──────────────────────────────────────────
            var nfoPath = Path.Combine(seriesDir, "tvshow.nfo");
            if (rawMovieNfo is not null)
            {
                try
                {
                    string nfoXml;
                    if (System.IO.File.Exists(nfoPath))
                    {
                        // Jellyfin wrote a tvshow.nfo — patch it with movie identity fields
                        var jellyfinNfo = await System.IO.File.ReadAllTextAsync(nfoPath, ct);
                        nfoXml = PatchTvShowNfo(jellyfinNfo, rawMovieNfo);
                        _logger.LogInformation("[Metadata] patched Jellyfin tvshow.nfo at {Path}", nfoPath);
                    }
                    else
                    {
                        // Scan didn't produce an NFO (e.g. no online match) — convert from scratch
                        nfoXml = ConvertMovieNfoToTvShow(rawMovieNfo, seriesDir);
                        _logger.LogInformation("[Metadata] no Jellyfin NFO found; wrote converted NFO at {Path}", nfoPath);
                    }

                    await System.IO.File.WriteAllTextAsync(nfoPath, nfoXml, Encoding.UTF8, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Metadata] failed to patch NFO at {Path}", nfoPath);
                }
            }

            // ── Copy images + season01-poster ─────────────────────────────
            if (movieDir is not null)
                CopySeriesImages(movieDir, seriesDir);
        }
    }

    /// <summary>
    /// Takes the tvshow.nfo Jellyfin generated after the scan and overwrites
    /// the identity fields with values from the source movie.nfo, preserving
    /// everything Jellyfin populated (episodeguide, tvdbid, season structure, etc.).
    /// </summary>
    private static string PatchTvShowNfo(string jellyfinNfoXml, string movieNfoXml)
    {
        var tvDoc = XDocument.Parse(jellyfinNfoXml);
        var tvRoot = tvDoc.Root ?? throw new InvalidOperationException("tvshow.nfo has no root");

        var movieRoot = XDocument.Parse(movieNfoXml).Root
            ?? throw new InvalidOperationException("movie.nfo has no root");

        string? MovieVal(string name) =>
            movieRoot.Elements(name).FirstOrDefault()?.Value?.Trim() is { Length: > 0 } v ? v : null;

        // Helper: set or replace a single element value in the tvshow NFO
        void Set(string name, string? value)
        {
            if (value is null) return;
            var el = tvRoot.Element(name);
            if (el is not null)
                el.Value = value;
            else
                tvRoot.AddFirst(new XElement(name, value));
        }

        // Helper: replace all instances of a repeating element
        void SetAll(string name, IEnumerable<string> values)
        {
            tvRoot.Elements(name).Remove();
            foreach (var v in values)
                tvRoot.Add(new XElement(name, v));
        }

        // ── Identity fields from the movie NFO ───────────────────────────
        Set("title", MovieVal("title"));
        Set("originaltitle", MovieVal("originaltitle"));
        Set("plot", MovieVal("plot"));
        Set("outline", MovieVal("plot"));     // tvshow.nfo mirrors plot into outline
        Set("tagline", MovieVal("tagline"));
        Set("year", MovieVal("year"));
        Set("premiered", MovieVal("premiered"));
        Set("releasedate", MovieVal("releasedate"));
        Set("rating", MovieVal("rating"));
        Set("mpaa", MovieVal("mpaa"));

        // movie.nfo uses <imdbid>; tvshow.nfo uses <imdb_id>
        Set("imdb_id", MovieVal("imdbid"));
        Set("tmdbid", MovieVal("tmdbid"));

        // Repeating elements — replace wholesale
        SetAll("genre", movieRoot.Elements("genre").Select(e => e.Value));
        SetAll("studio", movieRoot.Elements("studio").Select(e => e.Value));
        SetAll("tag", movieRoot.Elements("tag").Select(e => e.Value));
        SetAll("country", movieRoot.Elements("country").Select(e => e.Value));
        SetAll("trailer", movieRoot.Elements("trailer").Select(e => e.Value));

        return tvDoc.ToString();
    }

    /// <summary>
    /// Fallback: builds a tvshow.nfo from scratch when Jellyfin didn't produce one.
    /// </summary>
    private static string ConvertMovieNfoToTvShow(string movieNfoXml, string seriesDir)
    {
        var src = XDocument.Parse(movieNfoXml).Root
            ?? throw new InvalidOperationException("NFO has no root element");

        string? Val(string name) =>
            src.Elements(name).FirstOrDefault()?.Value?.Trim() is { Length: > 0 } v ? v : null;

        var plot = Val("plot");

        var tvRoot = new XElement("tvshow",
            plot is not null ? new XElement("plot", plot) : null,
            plot is not null ? new XElement("outline", plot) : null,
            new XElement("lockdata", Val("lockdata") ?? "false"),
            new XElement("dateadded", Val("dateadded") ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")),
            new XElement("title", Val("title") ?? ""),
            new XElement("originaltitle", Val("originaltitle") ?? ""),
            src.Elements("trailer").Select(e => new XElement("trailer", e.Value)),
            Val("rating") is { } rating ? new XElement("rating", rating) : null,
            Val("year") is { } year ? new XElement("year", year) : null,
            Val("mpaa") is { } mpaa ? new XElement("mpaa", mpaa) : null,
            Val("imdbid") is { } imdb ? new XElement("imdb_id", imdb) : null,
            Val("tmdbid") is { } tmdb ? new XElement("tmdbid", tmdb) : null,
            Val("premiered") is { } pre ? new XElement("premiered", pre) : null,
            Val("releasedate") is { } rel ? new XElement("releasedate", rel) : null,
            new XElement("runtime", "0"),
            Val("tagline") is { } tl ? new XElement("tagline", tl) : null,
            src.Elements("country").Select(e => new XElement("country", e.Value)),
            src.Elements("genre").Select(e => new XElement("genre", e.Value)),
            src.Elements("studio").Select(e => new XElement("studio", e.Value)),
            src.Elements("tag").Select(e => new XElement("tag", e.Value)),
            new XElement("art",
                new XElement("poster", Path.Combine(seriesDir, "folder.jpg")),
                new XElement("fanart", Path.Combine(seriesDir, "backdrop.jpg"))),
            new XElement("id", Val("tvdbid") ?? Val("imdbid") ?? ""),
            new XElement("season", "-1"),
            new XElement("episode", "-1"),
            new XElement("status", "Ended")
        );

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            tvRoot).ToString();
    }

    /// <summary>
    /// Copies standard series artwork from the movie folder into the series
    /// folder, then creates season01-poster from the first poster found.
    /// </summary>
    private void CopySeriesImages(string movieDir, string seriesDir)
    {
        string? copiedPosterDest = null;

        foreach (var imageName in SeriesImageNames)
        {
            foreach (var ext in ImageExtensions)
            {
                var src = Path.Combine(movieDir, imageName + ext);
                if (!System.IO.File.Exists(src))
                    continue;

                var dest = Path.Combine(seriesDir, imageName + ext);
                try
                {
                    System.IO.File.Copy(src, dest, overwrite: true);
                    _logger.LogInformation("[Metadata] copied image {Src} → {Dest}", src, dest);

                    if (copiedPosterDest is null &&
                        imageName is "poster" or "folder" or "cover" or "default")
                    {
                        copiedPosterDest = dest;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Metadata] failed to copy image {Src}", src);
                }

                break; // first matching extension wins
            }
        }

        // ── season01-poster ───────────────────────────────────────────────
        if (copiedPosterDest is not null)
        {
            var seasonPosterDest = Path.Combine(
                seriesDir,
                "season01-poster" + Path.GetExtension(copiedPosterDest));
            try
            {
                System.IO.File.Copy(copiedPosterDest, seasonPosterDest, overwrite: true);
                _logger.LogInformation("[Metadata] created season poster {Dest}", seasonPosterDest);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Metadata] failed to create season poster {Dest}", seasonPosterDest);
            }
        }
        else
        {
            _logger.LogWarning(
                "[Metadata] no poster image found in {Dir} — season01-poster skipped", movieDir);
        }
    }

    // ── Existing helpers (unchanged) ──────────────────────────────────────────

    [HttpGet("script")]
    [Produces("text/javascript")]
    [AllowAnonymous]
    public IActionResult GetScript()
    {
        var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream("MovieSplitter.Plugin.plugin.js");
        if (stream is null)
        {
            _logger.LogError("[MovieSplitter] Error fetching client script.");
            return NotFound();
        }

        return File(stream, "text/javascript");
    }

    [HttpGet("TestOllama")]
    [Authorize]
    public async Task<ActionResult<OllamaTestResult>> TestOllama(
        [FromQuery] string ollamaUrl,
        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res = await http.GetAsync(new Uri(ollamaUrl.TrimEnd('/') + "/api/tags"), ct);
            return Ok(new OllamaTestResult(res.IsSuccessStatusCode, null));
        }
        catch (Exception ex)
        {
            return Ok(new OllamaTestResult(false, ex.Message));
        }
    }

    private async Task<string?> GetSubtitleTextAsync(MediaStream stream)
    {
        if (stream.Type != MediaStreamType.Subtitle ||
            string.IsNullOrWhiteSpace(stream.Path) ||
            !System.IO.File.Exists(stream.Path))
        {
            _logger.LogWarning("[Subtitle] stream invalid or file missing: {Path}", stream.Path);
            return null;
        }

        return await System.IO.File.ReadAllTextAsync(stream.Path);
    }

    private ActionResult<string> GetLanguage()
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            _logger.LogInformation("user is null");
            return Unauthorized();
        }

        return Ok(user.SubtitleLanguagePreference);
    }

    private User? GetCurrentUser()
    {
        var userIdString = User?.Claims?
            .FirstOrDefault(c => c.Type.ToLower().Contains("userid"))?
            .Value;

        if (Guid.TryParse(userIdString, out var userId))
            return _userManager.GetUserById(userId);

        _logger.LogWarning("invalid user ID {Id}", userIdString);
        return null;
    }
}

public record SplitItemResult(int EpisodesCreated, string? Message);
public record OllamaTestResult(bool Ok, string? Error);
