using MediaBrowser.Model.Entities;

namespace MovieSplitter.Services;

public interface ISubtitleLoader
{
  Task<string?> LoadSubtitleTextAsync(MediaStream stream, CancellationToken ct = default);
}
