using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

public enum BoundaryConfidence
{
  Low,
  Medium,
  Strong,
  Chapter
}

public record BoundaryCandidate(TimeSpan Time, BoundaryConfidence Strength);

public interface IBoundaryDetectionService
{
  Task<(TimeSpan Start, TimeSpan End)?> DetectCreditsAsync(
      string ffmpegPath,
      string videoPath,
      IReadOnlyList<SubtitleCue> cues,
      TimeSpan totalDuration,
      CancellationToken ct);

  List<BoundaryCandidate> GenerateCandidates(
      IChapterManager chapterManager,
      Movie item,
      IReadOnlyList<SubtitleCue> cues,
      TimeSpan totalDuration,
      TimeSpan targetEpisode,
      TimeSpan boundaryWindow,
      (TimeSpan, TimeSpan)? credits);

  Task<List<BoundaryCandidate>> ValidateCandidatesAsync(
      string ffmpegPath,
      string videoPath,
      List<BoundaryCandidate> candidates,
      double preStartSeconds,
      CancellationToken ct);

  List<TimeSpan> EnforceMinimumEpisodeLength(
      IEnumerable<TimeSpan> boundaries,
      TimeSpan minEpisodeLength,
      TimeSpan totalDuration,
      (TimeSpan, TimeSpan)? credits);
}

public class BoundaryDetectionService : IBoundaryDetectionService
{
  public static readonly TimeSpan BoundaryWindow =
      TimeSpan.FromMinutes(10);

  public static readonly TimeSpan CandidateGapThreshold =
      TimeSpan.FromSeconds(20);

  public static readonly TimeSpan StrongGapThreshold =
      TimeSpan.FromSeconds(60);

  private const double SceneChangeThreshold = 0.5;
  private const double SceneMatchToleranceSeconds = 3.0;
  private const double SceneCoalesceSeconds = 1.0;

  public BoundaryDetectionService(ILogger logger)
  {
    _logger = logger;
  }

  private readonly ILogger _logger;


  public async Task<(TimeSpan Start, TimeSpan End)?> DetectCreditsAsync(
      string ffmpegPath,
      string videoPath,
      IReadOnlyList<SubtitleCue> cues,
      TimeSpan totalDuration,
      CancellationToken ct)
  {
    var videoCredits = await CreditsOnsetDetector.DetectAsync(
        ffmpegPath, videoPath, totalDuration, cues, _logger, ct);

    if (videoCredits is not null)
      return (videoCredits.Value, totalDuration);

    return DetectCreditsFromSubtitles(_logger, cues, totalDuration);
  }

  public List<BoundaryCandidate> GenerateCandidates(
      IChapterManager chapterManager,
      Movie item,
      IReadOnlyList<SubtitleCue> cues,
      TimeSpan totalDuration,
      TimeSpan targetEpisode,
      TimeSpan boundaryWindow,
      (TimeSpan, TimeSpan)? credits)
  {
    var chapterCandidates = FindChapterCandidates(
        chapterManager,
        item,
        totalDuration,
        targetEpisode,
        boundaryWindow,
        credits);

    if (chapterCandidates.Count > 0)
    {
      _logger.LogInformation(
          "[Boundary] using chapter fast-path candidates={Count}",
          chapterCandidates.Count);

      return chapterCandidates;
    }

    return FindSubtitleCandidates(
        cues,
        totalDuration,
        targetEpisode,
        boundaryWindow,
        credits);
  }

  public async Task<List<BoundaryCandidate>> ValidateCandidatesAsync(
      string ffmpegPath,
      string videoPath,
      List<BoundaryCandidate> candidates,
      double preStartSeconds,
      CancellationToken ct)
  {
    if (candidates is null || candidates.Count == 0)
      return new List<BoundaryCandidate>();

    List<TimeSpan> sceneChanges;
    try
    {
      sceneChanges = await GetSceneChangeTimesAsync(
          _logger,
          ffmpegPath,
          videoPath,
          ct);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "[Boundary] ffmpeg scene-change scan failed — skipping scene validation");
      return new List<BoundaryCandidate>();
    }

    if (sceneChanges.Count == 0)
    {
      _logger.LogInformation("[Boundary] no scene changes found — skipping");
      return new List<BoundaryCandidate>();
    }

    var matched = new List<BoundaryCandidate>();

    foreach (var cand in candidates)
    {
      var nearest = sceneChanges
          .OrderBy(s => Math.Abs((s - cand.Time).TotalSeconds))
          .First();

      var distance = Math.Abs((nearest - cand.Time).TotalSeconds);

      if (distance <= SceneMatchToleranceSeconds)
      {
        var newStart = nearest - TimeSpan.FromSeconds(preStartSeconds);
        if (newStart < TimeSpan.Zero)
          newStart = TimeSpan.Zero;

        _logger.LogInformation("[Boundary] candidate {Old} matched scene-change {Scene} (dist={Dist}s) → start at {Start}",
            cand.Time, nearest, distance, newStart);

        matched.Add(new BoundaryCandidate(newStart, cand.Strength));
      }
      else
      {
        _logger.LogDebug("[Boundary] candidate {Old} not near scene-change (nearest {Scene}, dist={Dist}s) — skipping",
            cand.Time, nearest, distance);
      }
    }

    return matched
        .DistinctBy(x => Math.Round(x.Time.TotalSeconds / 30))
        .OrderBy(x => x.Time)
        .ToList();
  }

  public List<TimeSpan> EnforceMinimumEpisodeLength(
      IEnumerable<TimeSpan> boundaries,
      TimeSpan minEpisodeLength,
      TimeSpan totalDuration,
      (TimeSpan, TimeSpan)? credits)
  {
    var end = credits?.Item1 ?? totalDuration;

    var times = boundaries
        .Where(t => t < end)
        .OrderBy(t => t)
        .ToList();

    var final = new List<TimeSpan>();
    var prev = TimeSpan.Zero;

    foreach (var t in times)
    {
      if ((t - prev) < minEpisodeLength)
        continue;

      final.Add(t);
      prev = t;
    }

    while (final.Count > 0)
    {
      var lastStart = final.Last();
      var lastLen = end - lastStart;
      if (lastLen >= minEpisodeLength)
        break;

      final.RemoveAt(final.Count - 1);
    }

    return final;
  }

  private List<BoundaryCandidate> FindChapterCandidates(
      IChapterManager chapterManager,
      Movie item,
      TimeSpan totalDuration,
      TimeSpan targetEpisode,
      TimeSpan boundaryWindow,
      (TimeSpan, TimeSpan)? credits)
  {
    var results = new List<BoundaryCandidate>();

    var chapters = chapterManager.GetChapters(item.Id)
        .Select(x => x.StartPositionTicks)
        .Where(x => x > 0)
        .Distinct()
        .OrderBy(x => x)
        .Select(TimeSpan.FromTicks)
        .ToList();

    if (chapters is null || chapters.Count == 0)
      return results;

    var creditsDuration =
        credits?.Item2 - credits?.Item1 ??
        TimeSpan.Zero;

    var episodeCount = EstimateEpisodeCount(
        totalDuration,
        targetEpisode,
        creditsDuration);

    var adjustedTarget = CalculateAdjustedTarget(
        totalDuration,
        episodeCount,
        creditsDuration);

    _logger.LogInformation(
        "[Chapters] found={Count} adjustedTarget={Target}",
        chapters.Count,
        adjustedTarget);

    for (
        var target = adjustedTarget;
        target < totalDuration - creditsDuration;
        target += adjustedTarget)
    {
      var best = chapters
          .Where(c =>
              c >= target - boundaryWindow &&
              c <= target + boundaryWindow)
          .OrderBy(c => Math.Abs((c - target).TotalSeconds))
          .FirstOrDefault();

      if (best == TimeSpan.Zero)
        continue;

      if (credits is not null &&
          best >= credits.Value.Item1)
      {
        continue;
      }

      _logger.LogInformation(
          "[Chapters] selected target={Target} actual={Actual}",
          target,
          best);

      results.Add(new BoundaryCandidate(best, BoundaryConfidence.Chapter));
    }

    return results
        .DistinctBy(x => Math.Round(x.Time.TotalSeconds / 30))
        .OrderBy(x => x.Time)
        .ToList();
  }

  private List<BoundaryCandidate> FindSubtitleCandidates(
      IReadOnlyList<SubtitleCue> cues,
      TimeSpan totalDuration,
      TimeSpan targetEpisode,
      TimeSpan boundaryWindow,
      (TimeSpan, TimeSpan)? credits)
  {
    var results = new List<BoundaryCandidate>();

    var creditsDuration =
        credits?.Item2 - credits?.Item1 ??
        TimeSpan.Zero;

    var episodeCount = EstimateEpisodeCount(
        totalDuration,
        targetEpisode,
        creditsDuration);

    var adjustedTarget = CalculateAdjustedTarget(
        totalDuration,
        episodeCount,
        creditsDuration);

    for (
        var target = adjustedTarget;
        target < totalDuration - creditsDuration;
        target += adjustedTarget)
    {
      var windowStart = target - boundaryWindow;
      var windowEnd = target + boundaryWindow;

      _logger.LogInformation(
          "[Subtitles] scanning target={Target} window={Start}-{End}",
          target,
          windowStart,
          windowEnd);

      BoundaryCandidate? best = null;

      for (int i = 0; i < cues.Count - 1; i++)
      {
        var cur = cues[i];
        var next = cues[i + 1];

        if (cur.End < windowStart)
          continue;

        if (cur.Start > windowEnd)
          break;

        var gap = next.Start - cur.End;

        TimeSpan? candidateTime = null;
        BoundaryConfidence confidence = BoundaryConfidence.Low;

        if (gap >= StrongGapThreshold)
        {
          candidateTime = cur.End;
          confidence = BoundaryConfidence.Strong;
        }
        else if (gap >= CandidateGapThreshold)
        {
          candidateTime = cur.End;
          confidence = BoundaryConfidence.Medium;
        }

        if (candidateTime is null)
          continue;

        if (credits is not null &&
            candidateTime >= credits.Value.Item1)
        {
          continue;
        }

        var distance =
            Math.Abs((candidateTime.Value - target).TotalSeconds);

        if (best is null)
        {
          best = new BoundaryCandidate(candidateTime.Value, confidence);
          continue;
        }

        var bestDistance =
            Math.Abs((best.Time - target).TotalSeconds);

        if (confidence > best.Strength ||
            (confidence == best.Strength &&
             distance < bestDistance))
        {
          best = new BoundaryCandidate(candidateTime.Value, confidence);
        }
      }

      if (best is not null)
      {
        _logger.LogInformation(
            "[Subtitles] selected target={Target} actual={Actual} strength={Strength}",
            target,
            best.Time,
            best.Strength);

        results.Add(best);
      }
    }

    return results
        .DistinctBy(x => Math.Round(x.Time.TotalSeconds / 30))
        .OrderBy(x => x.Time)
        .ToList();
  }

  public static int EstimateEpisodeCount(
      TimeSpan duration,
      TimeSpan targetEpisode,
      TimeSpan creditsDuration)
  {
    var usable = duration - creditsDuration;

    return Math.Max(
        1,
        (int)Math.Round(
            usable.TotalMinutes /
            targetEpisode.TotalMinutes));
  }

  public static TimeSpan CalculateAdjustedTarget(
      TimeSpan duration,
      int episodeCount,
      TimeSpan creditsDuration)
  {
    var usable = duration - creditsDuration;

    return TimeSpan.FromSeconds(
        usable.TotalSeconds / episodeCount);
  }

  private static (TimeSpan Start, TimeSpan End)? DetectCreditsFromSubtitles(
      ILogger logger,
      IReadOnlyList<SubtitleCue> cues,
      TimeSpan totalDuration)
  {
    if (cues.Count < 2)
      return null;

    var searchStart = TimeSpan.FromSeconds(
        totalDuration.TotalSeconds * 0.75);

    TimeSpan bestGapSize = TimeSpan.Zero;
    TimeSpan bestGapAt = TimeSpan.Zero;

    for (int i = 0; i < cues.Count - 1; i++)
    {
      var cur = cues[i];
      var next = cues[i + 1];

      if (cur.End < searchStart)
        continue;

      var gap = next.Start - cur.End;

      if (gap <= bestGapSize)
        continue;

      bestGapSize = gap;
      bestGapAt = cur.End;
    }

    if (bestGapSize < TimeSpan.FromSeconds(45))
    {
      var last = cues[cues.Count - 1];
      if (last.Start < TimeSpan.FromSeconds(totalDuration.TotalSeconds * 0.90))
      {
        logger.LogInformation("[Credits] subtitle fallback: no gap found — skipping");
        return null;
      }

      logger.LogInformation("[Credits] subtitle fallback: using last cue end {End}", last.End);
      return (last.End, totalDuration);
    }

    logger.LogInformation("[Credits] subtitle fallback: gap of {Gap:g} at {At}", bestGapSize, bestGapAt);
    return (bestGapAt, totalDuration);
  }

  private static async Task<string> RunFfmpegStderrAsync(
      string ffmpegPath,
      string args,
      CancellationToken ct)
  {
    using var proc = new System.Diagnostics.Process();
    proc.StartInfo = new System.Diagnostics.ProcessStartInfo
    {
      FileName = ffmpegPath,
      Arguments = args,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };

    proc.Start();
    var stderr = await proc.StandardError.ReadToEndAsync(ct);
    await proc.WaitForExitAsync(ct);

    if (proc.ExitCode != 0)
      throw new Exception($"ffmpeg exited {proc.ExitCode}:\n{stderr}");

    return stderr;
  }

  private static List<TimeSpan> ParseSceneChangeTimes(string stderr)
  {
    var results = new List<TimeSpan>();

    foreach (Match m in Regex.Matches(stderr, @"pts_time:([\d.]+)"))
    {
      if (!double.TryParse(
              m.Groups[1].Value,
              NumberStyles.Float,
              CultureInfo.InvariantCulture,
              out var relSec))
        continue;

      results.Add(TimeSpan.FromSeconds(relSec));
    }

    results.Sort();

    if (results.Count == 0)
      return results;

    var coalesced = new List<TimeSpan>();
    TimeSpan last = TimeSpan.Zero;

    foreach (var t in results)
    {
      if (coalesced.Count == 0)
      {
        coalesced.Add(t);
        last = t;
        continue;
      }

      if ((t - last).TotalSeconds <= SceneCoalesceSeconds)
        continue;

      coalesced.Add(t);
      last = t;
    }

    return coalesced;
  }

  private static async Task<List<TimeSpan>> GetSceneChangeTimesAsync(
      ILogger logger,
      string ffmpegPath,
      string videoPath,
      CancellationToken ct)
  {
    var args =
        $"-i \"{videoPath}\" " +
        $"-vf \"select='gt(scene,{SceneChangeThreshold.ToString(CultureInfo.InvariantCulture)})',showinfo\" " +
        "-vsync 0 -an -f null -";

    logger.LogInformation("[Boundary] scanning for scene changes (threshold={T})", SceneChangeThreshold);

    var stderr = await RunFfmpegStderrAsync(ffmpegPath, args, ct);
    return ParseSceneChangeTimes(stderr);
  }
}
