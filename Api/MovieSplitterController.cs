using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MovieSplitter.Detection;
using MovieSplitter.Services;

namespace MovieSplitter.Api;

[ApiController]
[Route("MovieSplitter")]
public class MovieSplitterController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _library;
    private readonly ISplitWorkflow _splitWorkflow;
    private readonly ILogger<MovieSplitterController> _logger;

    public MovieSplitterController(
        IUserManager userManager,
        ILibraryManager library,
        ISplitWorkflow splitWorkflow,
        ILogger<MovieSplitterController> logger)
    {
        _userManager = userManager;
        _library = library;
        _splitWorkflow = splitWorkflow;
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
        [FromQuery] double? targetEpisodeMinutes,
        CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is not Movie movie)
            return NotFound(new { Error = $"Item {itemId} is not a movie." });

        var config = Plugin.Instance!.Configuration;
        var effectiveTargetEpisodeMinutes = targetEpisodeMinutes ?? config.TargetEpisodeMinutes;

        var preferredLanguageResult = GetLanguage();
        var preferredLanguage = "eng";
        if (preferredLanguageResult.Result is UnauthorizedResult)
            preferredLanguage = preferredLanguageResult.Value!;

        var result = await _splitWorkflow.ExecuteAsync(
            movie,
            preferredLanguage,
            config,
            effectiveTargetEpisodeMinutes,
            ct);

        return result.Status switch
        {
            SplitResultStatus.Success => Ok(result),
            SplitResultStatus.NotFound => NotFound(new { Error = result.Message }),
            SplitResultStatus.BadRequest => BadRequest(new { Error = result.Message }),
            _ => StatusCode(500, new { Error = "Unexpected split result." })
        };
    }

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

public record OllamaTestResult(bool Ok, string? Error);
