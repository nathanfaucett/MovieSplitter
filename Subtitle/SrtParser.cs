using System.Text.RegularExpressions;

namespace MovieSplitter.Subtitle;

public static class SrtParser
{
    private static readonly Regex TimecodeRx = new(
        @"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s-->\s(\d{2}):(\d{2}):(\d{2}),(\d{3})",
        RegexOptions.Compiled);

    public static IReadOnlyList<SubtitleCue> Parse(string srtContent)
    {
        var cues = new List<SubtitleCue>();
        var blocks = srtContent.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var lines = block.Trim().Split('\n');
            if (lines.Length < 3) continue;

            var m = TimecodeRx.Match(lines[1]);
            if (!m.Success) continue;

            var start = new TimeSpan(0,
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
            var end = new TimeSpan(0,
                int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value),
                int.Parse(m.Groups[7].Value), int.Parse(m.Groups[8].Value));

            var text = string.Join(" ", lines.Skip(2)).Trim();
            cues.Add(new SubtitleCue(start, end, text));
        }

        return cues;
    }
}
