using System.IO;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace MovieSplitter.Services;

public class SubtitleLoader : ISubtitleLoader
{
  private readonly ILogger<SubtitleLoader> _logger;

  public SubtitleLoader(ILogger<SubtitleLoader> logger)
  {
    _logger = logger;
  }

  public async Task<string?> LoadSubtitleTextAsync(MediaStream stream, CancellationToken ct = default)
  {
    if (stream.Type != MediaStreamType.Subtitle ||
        string.IsNullOrWhiteSpace(stream.Path) ||
        !File.Exists(stream.Path))
    {
      _logger.LogWarning("[Subtitle] stream invalid or file missing: {Path}", stream.Path);
      return null;
    }

    return await File.ReadAllTextAsync(stream.Path, ct);
  }
}
