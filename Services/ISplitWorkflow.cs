using MediaBrowser.Controller.Entities.Movies;

namespace MovieSplitter.Services;

public interface ISplitWorkflow
{
  Task<SplitItemResult> ExecuteAsync(
      Movie movie,
      string preferredLanguage,
      PluginConfiguration config,
      double targetEpisodeMinutes,
      CancellationToken ct = default);
}
