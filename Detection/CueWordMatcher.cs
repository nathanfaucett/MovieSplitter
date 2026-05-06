using System.Text.RegularExpressions;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

public class CueWordMatcher
{
    private readonly IReadOnlyList<Regex> _patterns;

    public CueWordMatcher(string commaSeparatedPatterns)
    {
        _patterns = commaSeparatedPatterns
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new Regex(p.Trim(), RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
    }

    /// <summary>
    /// Returns timestamps where a cue phrase appears in the subtitles.
    /// </summary>
    public IEnumerable<TimeSpan> FindCueBoundaries(IReadOnlyList<SubtitleCue> cues)
    {
        foreach (var cue in cues)
            if (_patterns.Any(rx => rx.IsMatch(cue.Text)))
                yield return cue.Start;
    }
}
