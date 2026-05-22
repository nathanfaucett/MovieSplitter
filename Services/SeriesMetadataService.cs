using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace MovieSplitter.Services;

public class SeriesMetadataService : ISeriesMetadataService
{
  private readonly ILogger<SeriesMetadataService> _logger;

  private static readonly string[] SeriesImageNames =
  [
      "poster", "folder", "cover", "default",
        "fanart", "backdrop", "banner", "logo", "clearart", "disc", "landscape"
  ];

  private static readonly string[] ImageExtensions =
  [
      ".jpg", ".jpeg", ".png", ".webp", ".tbn"
  ];

  public SeriesMetadataService(ILogger<SeriesMetadataService> logger)
  {
    _logger = logger;
  }

  public async Task PatchSeriesMetadataAsync(Movie movie, string[] outputDirs, CancellationToken ct = default)
  {
    var safeName = string.Concat(movie.Name.Select(c =>
        Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim(' ', '.', '_', '-');

    var movieDir = Path.GetDirectoryName(movie.Path);

    string? rawMovieNfo = null;
    if (movieDir is not null)
    {
      var stem = Path.GetFileNameWithoutExtension(movie.Path);
      var sourceNfo = new[]
      {
                Path.Combine(movieDir, stem + ".nfo"),
                Path.Combine(movieDir, "movie.nfo"),
            }.FirstOrDefault(File.Exists);

      if (sourceNfo is not null)
      {
        try
        {
          rawMovieNfo = await File.ReadAllTextAsync(sourceNfo, ct);
          _logger.LogInformation("[Metadata] read source NFO from {Src}", sourceNfo);
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "[Metadata] failed to read source NFO");
        }
      }
      else
      {
        _logger.LogWarning("[Metadata] no movie.nfo found next to {Path}", movie.Path);
      }
    }

    foreach (var outputDir in outputDirs)
    {
      ct.ThrowIfCancellationRequested();

      var seriesDir = Path.Combine(outputDir, safeName);
      if (!Directory.Exists(seriesDir))
      {
        _logger.LogWarning("[Metadata] series dir not found: {Dir}", seriesDir);
        continue;
      }

      var nfoPath = Path.Combine(seriesDir, "tvshow.nfo");
      if (rawMovieNfo is not null)
      {
        try
        {
          string nfoXml;
          if (File.Exists(nfoPath))
          {
            var jellyfinNfo = await File.ReadAllTextAsync(nfoPath, ct);
            nfoXml = PatchTvShowNfo(jellyfinNfo, rawMovieNfo);
            _logger.LogInformation("[Metadata] patched Jellyfin tvshow.nfo at {Path}", nfoPath);
          }
          else
          {
            nfoXml = ConvertMovieNfoToTvShow(rawMovieNfo, seriesDir);
            _logger.LogInformation("[Metadata] no Jellyfin NFO found; wrote converted NFO at {Path}", nfoPath);
          }

          await File.WriteAllTextAsync(nfoPath, nfoXml, Encoding.UTF8, ct);
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "[Metadata] failed to patch NFO at {Path}", nfoPath);
        }
      }

      if (movieDir is not null)
        CopySeriesImages(movieDir, seriesDir);
    }
  }

  private static string PatchTvShowNfo(string jellyfinNfoXml, string movieNfoXml)
  {
    var tvDoc = XDocument.Parse(jellyfinNfoXml);
    var tvRoot = tvDoc.Root ?? throw new InvalidOperationException("tvshow.nfo has no root");

    var movieRoot = XDocument.Parse(movieNfoXml).Root
        ?? throw new InvalidOperationException("movie.nfo has no root");

    string? MovieVal(string name) =>
        movieRoot.Elements(name).FirstOrDefault()?.Value?.Trim() is { Length: > 0 } v ? v : null;

    void Set(string name, string? value)
    {
      if (value is null)
        return;

      var el = tvRoot.Element(name);
      if (el is not null)
        el.Value = value;
      else
        tvRoot.AddFirst(new XElement(name, value));
    }

    void SetAll(string name, IEnumerable<string> values)
    {
      tvRoot.Elements(name).Remove();
      foreach (var v in values)
        tvRoot.Add(new XElement(name, v));
    }

    Set("title", MovieVal("title"));
    Set("originaltitle", MovieVal("originaltitle"));
    Set("plot", MovieVal("plot"));
    Set("outline", MovieVal("plot"));
    Set("tagline", MovieVal("tagline"));
    Set("year", MovieVal("year"));
    Set("premiered", MovieVal("premiered"));
    Set("releasedate", MovieVal("releasedate"));
    Set("rating", MovieVal("rating"));
    Set("mpaa", MovieVal("mpaa"));
    Set("imdb_id", MovieVal("imdbid"));
    Set("tmdbid", MovieVal("tmdbid"));

    SetAll("genre", movieRoot.Elements("genre").Select(e => e.Value));
    SetAll("studio", movieRoot.Elements("studio").Select(e => e.Value));
    SetAll("tag", movieRoot.Elements("tag").Select(e => e.Value));
    SetAll("country", movieRoot.Elements("country").Select(e => e.Value));
    SetAll("trailer", movieRoot.Elements("trailer").Select(e => e.Value));

    return tvDoc.ToString();
  }

  private static string ConvertMovieNfoToTvShow(string movieNfoXml, string seriesDir)
  {
    var src = XDocument.Parse(movieNfoXml).Root
        ?? throw new InvalidOperationException("NFO has no root element");

    string? Val(string name) =>
        src.Elements(name).FirstOrDefault()?.Value?.Trim() is { Length: > 0 } v ? v : null;

    var plot = Val("plot");

    var tvRoot = new XElement("tvshow",
        plot is not null ? new XElement("plot", plot) : null,
        plot is not null ? new XElement("outline", plot) : null,
        new XElement("lockdata", Val("lockdata") ?? "false"),
        new XElement("dateadded", Val("dateadded") ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")),
        new XElement("title", Val("title") ?? string.Empty),
        new XElement("originaltitle", Val("originaltitle") ?? string.Empty),
        src.Elements("trailer").Select(e => new XElement("trailer", e.Value)),
        Val("rating") is { } rating ? new XElement("rating", rating) : null,
        Val("year") is { } year ? new XElement("year", year) : null,
        Val("mpaa") is { } mpaa ? new XElement("mpaa", mpaa) : null,
        Val("imdbid") is { } imdb ? new XElement("imdb_id", imdb) : null,
        Val("tmdbid") is { } tmdb ? new XElement("tmdbid", tmdb) : null,
        Val("premiered") is { } pre ? new XElement("premiered", pre) : null,
        Val("releasedate") is { } rel ? new XElement("releasedate", rel) : null,
        new XElement("runtime", "0"),
        Val("tagline") is { } tl ? new XElement("tagline", tl) : null,
        src.Elements("country").Select(e => new XElement("country", e.Value)),
        src.Elements("genre").Select(e => new XElement("genre", e.Value)),
        src.Elements("studio").Select(e => new XElement("studio", e.Value)),
        src.Elements("tag").Select(e => new XElement("tag", e.Value)),
        new XElement("art",
            new XElement("poster", Path.Combine(seriesDir, "folder.jpg")),
            new XElement("fanart", Path.Combine(seriesDir, "backdrop.jpg"))),
        new XElement("id", Val("tvdbid") ?? Val("imdbid") ?? string.Empty),
        new XElement("season", "-1"),
        new XElement("episode", "-1"),
        new XElement("status", "Ended")
    );

    return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), tvRoot).ToString();
  }

  private void CopySeriesImages(string movieDir, string seriesDir)
  {
    string? copiedPosterDest = null;

    foreach (var imageName in SeriesImageNames)
    {
      foreach (var ext in ImageExtensions)
      {
        var src = Path.Combine(movieDir, imageName + ext);
        if (!File.Exists(src))
          continue;

        var dest = Path.Combine(seriesDir, imageName + ext);
        try
        {
          File.Copy(src, dest, overwrite: true);
          _logger.LogInformation("[Metadata] copied image {Src} → {Dest}", src, dest);

          if (copiedPosterDest is null &&
              imageName is "poster" or "folder" or "cover" or "default")
          {
            copiedPosterDest = dest;
          }
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "[Metadata] failed to copy image {Src}", src);
        }

        break;
      }
    }

    if (copiedPosterDest is not null)
    {
      var seasonPosterDest = Path.Combine(
          seriesDir,
          "season01-poster" + Path.GetExtension(copiedPosterDest));
      try
      {
        File.Copy(copiedPosterDest, seasonPosterDest, overwrite: true);
        _logger.LogInformation("[Metadata] created season poster {Dest}", seasonPosterDest);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "[Metadata] failed to create season poster {Dest}", seasonPosterDest);
      }
    }
    else
    {
      _logger.LogWarning("[Metadata] no poster image found in {Dir} — season01-poster skipped", movieDir);
    }
  }
}
