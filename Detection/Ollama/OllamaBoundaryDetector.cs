using System.Text;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
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
    private readonly IChapterManager _chapterManager;

    public OllamaBoundaryDetector(
        PluginConfiguration config,
        OllamaClient client,
        ILogger logger,
        IChapterManager chapterManager)
    {
        _config = config;
        _client = client;
        _logger = logger;
        _chapterManager = chapterManager;

        _fallback =
            new HeuristicBoundaryDetector(
                config,
                logger,
                chapterManager);
    }

    public async Task<Boundaries> DetectAsync(
        Movie item,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        if (cues.Count == 0)
            return new Boundaries(
                Array.Empty<TimeSpan>(),
                null);

        var credits =
            BoundaryDetectionHelper.DetectCredits(
                _logger,
                cues,
                totalDuration);

        var targetEpisode =
            TimeSpan.FromMinutes(
                _config.TargetEpisodeMinutes);

        var candidates =
            BoundaryDetectionHelper.GenerateCandidates(
                _logger,
                _chapterManager,
                item,
                cues,
                totalDuration,
                targetEpisode,
                BoundaryDetectionHelper.BoundaryWindow,
                credits);

        try
        {
            _logger.LogInformation(
                "[Ollama] candidates={Count}",
                candidates.Count);

            var confirmed = new List<TimeSpan>();

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                // Chapter-based candidates are trusted
                if (candidate.Strength ==
                    BoundaryDetectionHelper.Confidence.Chapter)
                {
                    confirmed.Add(candidate.Time);
                    continue;
                }

                var context = cues
                    .Where(x =>
                        x.Start >= candidate.Time - BoundaryDetectionHelper.BoundaryWindow / 2 &&
                        x.End <= candidate.Time + BoundaryDetectionHelper.BoundaryWindow / 2)
                    .ToList();

                if (context.Count == 0)
                    continue;

                var prompt = BuildPrompt(
                    candidate.Time,
                    context,
                    targetEpisode);

                _logger.LogInformation(
                    "[Ollama][Prompt] target={Time} chars={Length}",
                    candidate.Time,
                    prompt.Length);

                var response =
                    await _client.GenerateAsync(
                        prompt,
                        ct);

                _logger.LogInformation(
                    "[Ollama][Response] target={Time}: {Response}",
                    candidate.Time,
                    response);

                if (TryParseBestSplitTime(
                    response,
                    out var bestTime))
                {
                    if (bestTime > TimeSpan.Zero &&
                        Math.Abs((bestTime - candidate.Time).TotalMinutes)
                        <= BoundaryDetectionHelper.BoundaryWindow.TotalMinutes * 2)
                    {
                        confirmed.Add(bestTime);
                    }
                }
            }

            var boundaries = confirmed
                .DistinctBy(t =>
                    Math.Round(t.TotalSeconds / 30))
                .OrderBy(t => t)
                .ToList();

            if (boundaries.Count == 0)
            {
                _logger.LogWarning(
                    "[Ollama] no validated boundaries → fallback");

                return await _fallback.DetectAsync(
                    item,
                    cues,
                    totalDuration,
                    ct);
            }

            return new Boundaries(
                boundaries,
                credits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[Ollama] error → fallback");

            return await _fallback.DetectAsync(
                item,
                cues,
                totalDuration,
                ct);
        }
    }

    private static string BuildPrompt(
        TimeSpan targetTime,
        List<SubtitleCue> cues,
        TimeSpan targetPartLength)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "You are helping split a movie into roughly equal parts.");

        sb.AppendLine(
            $"Target part length: ~{targetPartLength.TotalMinutes} minutes");

        sb.AppendLine(
            $"Approximate split time being evaluated: {targetTime:hh\\:mm\\:ss}");

        sb.AppendLine();
        sb.AppendLine("SUBTITLES AROUND THIS TIME:");
        sb.AppendLine("[start] text");

        foreach (var c in cues)
            sb.AppendLine(
                $"[{c.Start:hh\\:mm\\:ss}] {c.Text}");

        sb.AppendLine();
        sb.AppendLine("TASK:");
        sb.AppendLine(
            "Find the SINGLE best natural place to stop this part of the movie.");

        sb.AppendLine();
        sb.AppendLine(
            "Respond using this exact format only:");

        sb.AppendLine(
            "BEST_SPLIT: hh:mm:ss");

        sb.AppendLine("or");

        sb.AppendLine(
            "BEST_SPLIT: NONE");

        return sb.ToString();
    }

    private static bool TryParseBestSplitTime(
        string response,
        out TimeSpan time)
    {
        time = TimeSpan.Zero;

        var match =
            System.Text.RegularExpressions.Regex.Match(
                response.Trim(),
                @"BEST_SPLIT:\s*(?:NONE|(\d{1,2}:\d{2}:\d{2}))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        var timeStr = match.Groups[1].Value;

        if (string.IsNullOrWhiteSpace(timeStr))
            return false;

        return TimeSpan.TryParse(
            timeStr,
            out time);
    }
}
