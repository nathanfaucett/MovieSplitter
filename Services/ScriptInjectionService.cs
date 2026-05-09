using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MovieSplitter.Services;

/// <summary>
/// Injects a &lt;script&gt; tag for the Movie Splitter detail-page script into
/// Jellyfin's index.html at server startup, and removes it at shutdown.
///
/// The tag is wrapped in a sentinel comment block so it can be found and
/// removed cleanly without touching anything else in the file.
///
/// Docker note: the Jellyfin process must have write access to index.html.
/// If it doesn't, this service logs a warning and skips injection — the
/// plugin continues to function; only the detail-page button will be absent.
/// Workaround: volume-mount index.html so the plugin can write to it.
/// </summary>
public class ScriptInjectionService : IHostedService
{
    private const string SentinelStart = "<!-- MovieSplitter:start -->";
    private const string SentinelEnd = "<!-- MovieSplitter:end -->";
    private const string ScriptTag =
        "<script plugin=\"MovieSplitter\" defer=\"defer\" src=\"/MovieSplitter/script\"></script>";

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<ScriptInjectionService> _logger;

    public ScriptInjectionService(
        IApplicationPaths appPaths,
        ILogger<ScriptInjectionService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = FindIndexHtml();
            if (indexPath is null)
            {
                _logger.LogWarning("[MovieSplitter] Could not locate index.html — client script will not be injected.");
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexPath, Encoding.UTF8);

            // Remove any stale block first (handles re-starts without a clean shutdown)
            html = RemoveSentinelBlock(html);

            // Inject before </body>
            var injection = $"\n    {SentinelStart}\n    {ScriptTag}\n    {SentinelEnd}";
            html = html.Replace("</body>", injection + "\n</body>", StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(indexPath, html, Encoding.UTF8);
            _logger.LogInformation("[MovieSplitter] Injected client script into {Path}", indexPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex,
                "[MovieSplitter] Cannot write to index.html (permission denied). " +
                "To enable the detail-page button, grant the Jellyfin process write access " +
                "to the web directory, or volume-mount index.html in Docker.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MovieSplitter] Error injecting client script.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = FindIndexHtml();
            if (indexPath is null) return Task.CompletedTask;

            var html = File.ReadAllText(indexPath, Encoding.UTF8);
            var cleaned = RemoveSentinelBlock(html);

            if (cleaned != html)
            {
                File.WriteAllText(indexPath, cleaned, Encoding.UTF8);
                _logger.LogInformation("[MovieSplitter] Removed client script from {Path}", indexPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MovieSplitter] Could not remove client script on shutdown.");
        }

        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string RemoveSentinelBlock(string html)
    {
        while (true)
        {
            var start = html.IndexOf(SentinelStart, StringComparison.Ordinal);
            if (start < 0) break;

            var end = html.IndexOf(SentinelEnd, start, StringComparison.Ordinal);
            if (end < 0) break;

            end += SentinelEnd.Length;

            // Also eat the surrounding newline/whitespace so we don't leave blank lines
            if (end < html.Length && html[end] == '\n') end++;

            html = html.Remove(start, end - start);
        }

        return html;
    }

    /// <summary>
    /// Tries common locations for Jellyfin's index.html.
    /// Jellyfin exposes IApplicationPaths but not the web-root directly,
    /// so we walk up from the program directory and check known paths.
    /// </summary>
    private string? FindIndexHtml()
    {
        // Candidates relative to the program / data directories
        var candidates = new List<string>();

        // Jellyfin typically lives in the same parent as its web folder
        var programDir = AppContext.BaseDirectory;

        candidates.Add(Path.Combine(programDir, "jellyfin-web", "index.html"));
        candidates.Add(Path.Combine(programDir, "..", "jellyfin-web", "index.html"));

        // Common Linux package / Docker paths
        candidates.Add("/usr/share/jellyfin/web/index.html");
        candidates.Add("/jellyfin/jellyfin-web/index.html");

        // Windows default
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            candidates.Add(Path.Combine(localAppData, "Jellyfin", "Server", "jellyfin-web", "index.html"));

        foreach (var candidate in candidates)
        {
            var normalized = Path.GetFullPath(candidate);
            if (File.Exists(normalized))
            {
                _logger.LogDebug("[MovieSplitter] Found index.html at {Path}", normalized);
                return normalized;
            }
        }

        return null;
    }
}
