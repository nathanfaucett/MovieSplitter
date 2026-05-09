using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _library;
    private readonly ILogger<MovieSplitterController> _logger;

    public MovieSplitterController(
        IUserManager userManager,
        ILibraryManager library,
        ILogger<MovieSplitterController> logger)
    {
        _userManager = userManager;
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// Split a single movie by its Jellyfin item ID.
    /// POST /MovieSplitter/SplitItem?itemId={guid}
    /// </summary>
    [HttpPost("SplitItem")]
    [Authorize]
    public async Task<ActionResult<SplitItemResult>> SplitItem(
        [FromQuery] Guid itemId,
        CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is not Movie movie)
            return NotFound(new { Error = $"Item {itemId} is not a movie." });

        var config = Plugin.Instance!.Configuration;

        var preferredLanguageResult = GetLanguage();
        var preferredLanguage = "eng";
        if (preferredLanguageResult.Result is UnauthorizedResult result)
        {
            preferredLanguage = preferredLanguageResult.Value!;
        }



        var subtitle = movie.GetMediaStreams()
            .Where(x => x.Type == MediaStreamType.Subtitle && !string.IsNullOrWhiteSpace(x.Path))
            .OrderByDescending(x => x.Language == preferredLanguage)
            .ThenByDescending(x => x.Language == "eng")
            .FirstOrDefault();

        if (subtitle is null)
            return NotFound(new { Error = $"Item {itemId} does not have any subtitles {preferredLanguage}." });

        var srtContent = await GetSubtitleTextAsync(subtitle);
        if (srtContent is null)
            return BadRequest(new { Error = "No subtitles found for this movie." });

        var cues = SrtParser.Parse(srtContent);
        var totalDuration = movie.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(movie.RunTimeTicks.Value) : TimeSpan.Zero;

        _logger.LogInformation($"Creating boundary detector for {config.DetectorMode}");
        var detector = BoundaryDetectorFactory.Create(config, _logger);
        _logger.LogInformation($"Starting boundary detector {detector.Name}");
        var boundaries = await detector.DetectAsync(cues, totalDuration, ct);

        if (boundaries.StartTimes.Count == 0)
            return Ok(new SplitItemResult(0, "No episode start times detected."));

        _logger.LogInformation($"Finished boundary detector boundaries {boundaries.StartTimes.Count} credits {boundaries.Credits?.Item1}");

        var outputDirs = _library.GetVirtualFolders()
            .Where(x => x.CollectionType == CollectionTypeOptions.tvshows)
            .Select(x => x.Locations)
            .FirstOrDefault();

        if (outputDirs is null)
            return NotFound(new SplitItemResult(0, "No tv show folder found."));

        _logger.LogInformation($"Output locations {string.Join(", ", outputDirs)}");

        var ffmpegPath = Plugin.Instance!.GetFfmpegPath();
        var splitter = new FfmpegSplitter(ffmpegPath, _logger);
        _logger.LogInformation("Starting ffmpeg");
        var segments = await splitter.SplitAsync(
            movie.Path, boundaries.StartTimes, boundaries.Credits, totalDuration, outputDirs, movie.Name, ct);

        _library.QueueLibraryScan();

        return Ok(new SplitItemResult(segments.Count, null));
    }

    /// <summary>
    /// Return the script
    /// GET /MovieSplitter/script
    /// </summary>
    [HttpGet("script")]
    [Produces("text/javascript")]
    [AllowAnonymous]
    public IActionResult GetScript()
    {
        var assembly = typeof(Plugin).Assembly;
        var resource = "MovieSplitter.Plugin.plugin.js";

        var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            _logger.LogError("[MovieSplitter] Error fetching client script.");
            return NotFound();
        }

        return File(stream, "text/javascript");
    }

    /// <summary>
    /// Probe an Ollama server URL to verify connectivity.
    /// GET /MovieSplitter/TestOllama?ollamaUrl={url}
    /// </summary>
    [HttpGet("TestOllama")]
    [Authorize]
    public async Task<ActionResult<OllamaTestResult>> TestOllama(
        [FromQuery] string ollamaUrl,
        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res = await http.GetAsync(
                new Uri(ollamaUrl.TrimEnd('/') + "/api/tags"), ct);
            return Ok(new OllamaTestResult(res.IsSuccessStatusCode, null));
        }
        catch (Exception ex)
        {
            return Ok(new OllamaTestResult(false, ex.Message));
        }
    }

    private async Task<string?> GetSubtitleTextAsync(MediaStream stream)
    {
        if (stream.Type != MediaStreamType.Subtitle)
        {
            _logger.LogWarning("media is not a subtitle");
            return null;
        }

        if (string.IsNullOrWhiteSpace(stream.Path))
        {
            _logger.LogWarning("invalid path");
            return null;
        }

        if (!System.IO.File.Exists(stream.Path))
        {
            _logger.LogWarning("file does not exists");
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
        {
            return _userManager.GetUserById(userId);
        }
        _logger.LogWarning($"invalid user ID {userIdString}");

        return null;
    }
}

public record SplitItemResult(int EpisodesCreated, string? Message);
public record OllamaTestResult(bool Ok, string? Error);
