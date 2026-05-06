using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace MovieSplitter;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "Movie Splitter";
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

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
            Name = "moviesplitter",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        },
        new PluginPageInfo
        {
            Name = "moviesplitter.js",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.detailPagePlugin.js"
        }
    ];

    public string GetFfmpegPath()
    {
        // Jellyfin exposes the ffmpeg path via IMediaEncoder — resolve via DI
        // in a real build; falling back to PATH lookup here.
        return "ffmpeg";
    }
}
