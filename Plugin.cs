using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace MovieSplitter;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "Movie Splitter";
    public override Guid Id => Guid.Parse("b2be5f82-6324-4e02-a66c-6da5a160ac45");

    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name                 = $"{GetType().Namespace}",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            DisplayName          = "configPage.html",
        }
    ];

    public static string FindFfmpeg(ILogger logger)
    {
        var candidates = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/bin/ffmpeg",
            "ffmpeg"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                logger.LogInformation("Using FFmpeg at: {Path}", candidate);
                return candidate;
            }
        }

        logger.LogWarning("FFmpeg not found in common locations.");
        return "ffmpeg";
    }
}
