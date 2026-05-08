using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

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
        // Settings page — shown in Dashboard → Plugins → My Plugins
        new PluginPageInfo
        {
            Name                 = "moviesplitter",
            DisplayName          = "Movie Splitter",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        },

        // Detail-page button injection script.
        //
        // Jellyfin serves this file at /web/configurationpage?name=moviesplitterdetail
        // and — because it is a JS file registered as a plugin page — the web
        // client automatically loads and executes it on startup.
        //
        // Inside the script, pluginManager.register({ type: 'itemdetailoptions', … })
        // hooks into Jellyfin's item-detail view to add the "Split into episodes"
        // button panel via the officially supported 10.9+ extension point.
        //
        // EnableInMainMenu = false keeps it out of the sidebar navigation.
        new PluginPageInfo
        {
            Name                 = "moviesplitterdetail",
            DisplayName          = "Movie Splitter Detail",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.detailPagePlugin.js",
            EnableInMainMenu     = false
        }
    ];

    public string GetFfmpegPath()
    {
        // Jellyfin exposes the ffmpeg path via IMediaEncoder — resolve via DI
        // in a real build; falling back to PATH lookup here.
        return "ffmpeg";
    }
}
