using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using MovieSplitter.Services;
using MovieSplitter.Splitting;

namespace MovieSplitter;

public class ServiceRegistration : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddScoped<ISubtitleLoader, SubtitleLoader>();
        serviceCollection.AddScoped<IFfmpegSplitter, FfmpegSplitter>();
        serviceCollection.AddScoped<ILibraryScanService, LibraryScanService>();
        serviceCollection.AddScoped<ISeriesMetadataService, SeriesMetadataService>();
        serviceCollection.AddScoped<ISplitWorkflow, SplitWorkflow>();
        serviceCollection.AddHostedService<ScriptInjectionService>();
    }
}
