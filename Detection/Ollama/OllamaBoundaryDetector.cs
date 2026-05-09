using System.Text;
using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection.Ollama;

public class OllamaBoundaryDetector : IBoundaryDetector
{
    public string Name => "Ollama";

    private readonly OllamaClient _client;
    private readonly HeuristicBoundaryDetector _fallback;
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    public OllamaBoundaryDetector(
        PluginConfiguration config,
        OllamaClient client,
        ILogger logger)
    {
        _config = config;
        _client = client;
        _logger = logger;
        _fallback = new HeuristicBoundaryDetector(config, logger);
    }

    public async Task<Boundaries> DetectAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        if (cues.Count == 0)
            return new Boundaries(Array.Empty<TimeSpan>(), null);

        var credits = HeuristicBoundaryDetector.DetectCredits(_logger, cues, totalDuration);
        var targetEpisode = TimeSpan.FromMinutes(_config.TargetEpisodeMinutes);
        var candidates = HeuristicBoundaryDetector.FindCandidates(_logger, cues, totalDuration, targetEpisode, HeuristicBoundaryDetector.BoundaryWindow, credits);

        try
        {
            _logger.LogInformation("[Ollama] candidates={Count}", candidates.Count);

            var confirmedBoundaries = new List<TimeSpan>();

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var context = cues
                    .Where(x =>
                        x.Start >= candidate.Time - HeuristicBoundaryDetector.BoundaryWindow / 2 &&
                        x.End <= candidate.Time + HeuristicBoundaryDetector.BoundaryWindow / 2)
                    .ToList();

                if (context.Count == 0)
                    continue;

                var prompt = BuildPrompt(candidate.Time, context, targetEpisode);

                _logger.LogInformation(
                    "[Ollama][Prompt] target={Time} chars={Length}\n{Prompt}",
                    candidate.Time, prompt.Length, prompt);

                var response = await _client.GenerateAsync(prompt, ct);

                _logger.LogInformation(
                    "[Ollama][Response] target={Time}: {Response}",
                    candidate.Time, response);

                if (TryParseBestSplitTime(response, out var bestTime))
                {
                    if (bestTime > TimeSpan.Zero &&
                        Math.Abs((bestTime - candidate.Time).TotalMinutes) <= HeuristicBoundaryDetector.BoundaryWindow.TotalMinutes * 2)
                    {
                        confirmedBoundaries.Add(bestTime);
                    }
                }
            }

            // Deduplicate (30-second tolerance)
            var boundaries = confirmedBoundaries
                .DistinctBy(t => Math.Round(t.TotalSeconds / 30))
                .OrderBy(t => t)
                .ToList();

            // Fallback: largest natural gaps if model found nothing
            if (boundaries.Count == 0)
            {
                _logger.LogWarning("[Ollama] No good split points found by model → using largest gaps");
                boundaries = FindLargestGaps(cues, credits, targetEpisode);
            }

            return new Boundaries(boundaries, credits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ollama] Error, falling back to heuristic");
            return await _fallback.DetectAsync(cues, totalDuration, ct);
        }
    }

    private static string BuildPrompt(
        TimeSpan targetTime,
        List<SubtitleCue> cues,
        TimeSpan targetPartLength)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are helping split a movie into roughly equal parts.");
        sb.AppendLine($"Target part length: ~{targetPartLength.TotalMinutes} minutes");
        sb.AppendLine($"Approximate split time being evaluated: {targetTime:hh\\:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("SUBTITLES AROUND THIS TIME:");
        sb.AppendLine("[start] text");

        foreach (var c in cues)
            sb.AppendLine($"[{c.Start:hh\\:mm\\:ss}] {c.Text}");

        sb.AppendLine();
        sb.AppendLine("TASK:");
        sb.AppendLine("Find the SINGLE best natural place to stop this part of the movie.");
        sb.AppendLine("Good split points are usually:");
        sb.AppendLine("- End of a scene");
        sb.AppendLine("- End of an act / major sequence");
        sb.AppendLine("- After a significant event or cliffhanger");
        sb.AppendLine("- Long pause / silence between scenes");
        sb.AppendLine("- Natural narrative break");
        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("• Choose one of the subtitle timestamps as the split point.");
        sb.AppendLine("• If this window does NOT contain a good natural break, reply with NONE.");
        sb.AppendLine();
        sb.AppendLine("Respond using this exact format only:");
        sb.AppendLine("BEST_SPLIT: hh:mm:ss");
        sb.AppendLine("or");
        sb.AppendLine("BEST_SPLIT: NONE");

        return sb.ToString();
    }

    private static bool TryParseBestSplitTime(string response, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var trimmed = response.Trim();

        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"BEST_SPLIT:\s*(?:NONE|(\d{1,2}:\d{2}:\d{2}))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        var timeStr = match.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(timeStr) ||
            timeStr.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return false;

        return TimeSpan.TryParse(timeStr, out time);
    }

    private List<TimeSpan> FindLargestGaps(
        IReadOnlyList<SubtitleCue> cues,
        (TimeSpan, TimeSpan)? credits,
        TimeSpan targetEpisode)
    {
        var gaps = new List<(TimeSpan Time, TimeSpan Duration)>();

        for (int i = 0; i < cues.Count - 1; i++)
        {
            var gap = cues[i + 1].Start - cues[i].End;

            if (gap > TimeSpan.FromSeconds(40))
            {
                var splitTime = cues[i].End;

                // Skip credits area
                if (credits.HasValue && splitTime >= credits.Value.Item1)
                    continue;

                gaps.Add((splitTime, gap));
            }
        }

        return gaps
            .OrderByDescending(g => g.Duration)
            .Select(g => g.Time)
            .Take(5)
            .ToList();
    }
}
