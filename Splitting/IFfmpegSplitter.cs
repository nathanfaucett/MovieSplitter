namespace MovieSplitter.Splitting;

public interface IFfmpegSplitter
{
  Task<IReadOnlyList<EpisodeSegment>> SplitAsync(
      string inputPath,
      IReadOnlyList<TimeSpan> startTimes,
      (TimeSpan Start, TimeSpan End)? credits,
      TimeSpan totalDuration,
      string[] outputDirs,
      string seriesName,
      CancellationToken ct);
}
