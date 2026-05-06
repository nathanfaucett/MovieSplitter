using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

/// <summary>
/// All boundary detection strategies implement this contract.
/// DetectAsync returns sorted split timestamps (excludes 0 and totalDuration).
/// </summary>
public interface IBoundaryDetector
{
    string Name { get; }

    Task<IReadOnlyList<TimeSpan>> DetectAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default);
}
