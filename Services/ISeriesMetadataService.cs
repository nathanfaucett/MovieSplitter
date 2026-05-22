using MediaBrowser.Controller.Entities.Movies;

namespace MovieSplitter.Services;

public interface ISeriesMetadataService
{
  Task PatchSeriesMetadataAsync(Movie movie, string[] outputDirs, CancellationToken ct = default);
}
