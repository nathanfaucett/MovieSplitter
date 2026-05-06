using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MovieSplitter.Detection;
using MovieSplitter.Splitting;
using MovieSplitter.Subtitle;
using MovieSplitter.Tasks;

namespace MovieSplitter.Api;

[ApiController]
[Route("MovieSplitter")]
[Authorize(Policy = "RequiresElevation")]
public class MovieSplitterController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly ILogger<MovieSplitterController> _logger;

    public MovieSplitterController(
        ILibraryManager library,
        ILogger<MovieSplitterController> logger)
    {
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// Split a single movie by its Jellyfin item ID.
    /// POST /MovieSplitter/SplitItem?itemId={guid}
    /// </summary>
    [HttpPost("SplitItem")]
    public async Task<ActionResult<SplitItemResult>> SplitItem(
        [FromQuery] Guid itemId,
        CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is not Movie movie)
            return NotFound(new { Error = $"Item {itemId} is not a movie." });

        var config = Plugin.Instance!.Configuration;

        var loader = new SubtitleLoader(config, _logger);
        var srtContent = await loader.LoadAsync(movie.Path, ct);
        if (srtContent is null)
            return BadRequest(new { Error = "No subtitles found for this movie." });

        var cues = SrtParser.Parse(srtContent);
        var totalDuration = movie.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(movie.RunTimeTicks.Value) : TimeSpan.Zero;

        var detector = BoundaryDetectorFactory.Create(config, _logger);
        var boundaries = await detector.DetectAsync(cues, totalDuration, ct);

        if (boundaries.Count == 0)
            return Ok(new SplitItemResult(0, "No episode boundaries detected."));

        var outputDir = Path.Combine(Path.GetDirectoryName(movie.Path)!, config.OutputSubfolder);
        var ffmpegPath = Plugin.Instance!.GetFfmpegPath();
        var splitter = new FfmpegSplitter(ffmpegPath, _logger);
        var segments = await splitter.SplitAsync(
            movie.Path, boundaries, totalDuration, outputDir, movie.Name, ct);

        _library.QueueLibraryScan();

        return Ok(new SplitItemResult(segments.Count, null));
    }

    /// <summary>
    /// Probe an Ollama server URL to verify connectivity.
    /// GET /MovieSplitter/TestOllama?ollamaUrl={url}
    /// </summary>
    [HttpGet("TestOllama")]
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
}

public record SplitItemResult(int EpisodesCreated, string? Message);
public record OllamaTestResult(bool Ok, string? Error);
