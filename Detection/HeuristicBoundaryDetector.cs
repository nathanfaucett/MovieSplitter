using J2N.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

public class HeuristicBoundaryDetector : IBoundaryDetector
{
    public string Name => "Heuristic";

    public static readonly TimeSpan BoundaryWindow =
        TimeSpan.FromMinutes(10);

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    public HeuristicBoundaryDetector(
        PluginConfiguration config,
        ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task<Boundaries> DetectAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        var credits = DetectCredits(_logger, cues, totalDuration);
        var candidates = FindCandidates(_logger, cues, totalDuration, TimeSpan.FromMinutes(_config.TargetEpisodeMinutes), BoundaryWindow, credits);

        var creditsDuration = credits is not null
            ? credits.Value.Item2 - credits.Value.Item1
            : TimeSpan.Zero;
        var minEpisodes = (int)Math.Floor((totalDuration.TotalMinutes - creditsDuration.TotalMinutes) /
            _config.TargetEpisodeMinutes);

        var boundaries = new List<TimeSpan>();
        var loopLimit = Math.Max(minEpisodes * 2, candidates.Count);
        var loopCount = 0;

        while (boundaries.Count < minEpisodes && candidates.Count > 0 && loopCount < loopLimit)
        {
            loopCount++;
            var candidate = candidates[0];

            if (boundaries.Any(b => Math.Abs((b - candidate.Time).TotalMinutes) < BoundaryWindow.TotalMinutes))
                continue;

            candidates.RemoveAt(0);
            boundaries.Add(candidate.Time);
        }

        return Task.FromResult(new Boundaries(boundaries, credits));
    }

    private static readonly TimeSpan CandidateGapThreshold =
        TimeSpan.FromSeconds(20);

    private static readonly TimeSpan StrongGapThreshold =
        TimeSpan.FromSeconds(60);

    public enum Confidence
    {
        Low,
        Medium,
        Strong
    }

    public record Candidate(
        TimeSpan Time,
        Confidence Strength);

    public static List<Candidate> FindCandidates(
        ILogger _logger,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        TimeSpan targetEpisode,
        TimeSpan targetWindowRange,
        (TimeSpan, TimeSpan)? credits)
    {
        var results = new List<Candidate>();
        var actualTotalDuration = totalDuration - (credits?.Item2 - credits?.Item1 ?? TimeSpan.Zero);
        var adjustedTargetEpisode = TimeSpan.FromMinutes(totalDuration.TotalMinutes / Math.Round(totalDuration.TotalMinutes / targetEpisode.TotalMinutes));

        for (
            var target = adjustedTargetEpisode;
            target < actualTotalDuration;
            target += adjustedTargetEpisode)
        {
            var windowStart = target - targetWindowRange;
            var windowEnd = target + targetWindowRange;

            _logger.LogInformation(
                "[Prod] scanning target={Target} window={Start}-{End}",
                target,
                windowStart,
                windowEnd);

            Candidate? best = null;

            for (int i = 0; i < cues.Count - 1; i++)
            {
                var cur = cues[i];
                var next = cues[i + 1];

                // Fast skips
                if (cur.End < windowStart)
                    continue;

                if (cur.Start > windowEnd)
                    break;

                var gap = next.Start - cur.End;

                TimeSpan? candidateTime = null;
                Confidence confidence = Confidence.Low;

                // Strong pause
                if (gap >= StrongGapThreshold)
                {
                    candidateTime = cur.End;
                    confidence = Confidence.Strong;
                }
                // Medium pause
                else if (gap >= CandidateGapThreshold)
                {
                    candidateTime = cur.End;
                    confidence = Confidence.Medium;
                }
                // Boundary text
                else if (IsBoundaryText(cur.Text, next.Text))
                {
                    candidateTime = cur.End;
                    confidence = Confidence.Strong;
                }

                if (candidateTime is null)
                    continue;

                // Skip credits
                if (credits is not null &&
                    candidateTime >= credits?.Item1)
                {
                    continue;
                }

                var distance =
                    Math.Abs((candidateTime.Value - target).TotalSeconds);

                if (best is null)
                {
                    best = new Candidate(
                        candidateTime.Value,
                        confidence);

                    continue;
                }

                var bestDistance =
                    Math.Abs((best.Time - target).TotalSeconds);

                if (confidence > best.Strength ||
                    (confidence == best.Strength &&
                     distance < bestDistance))
                {
                    best = new Candidate(
                        candidateTime.Value,
                        confidence);
                }
            }

            if (best is not null)
            {
                _logger.LogInformation(
                    "[Prod] selected candidate target={Target} actual={Actual} strength={Strength}",
                    target,
                    best.Time,
                    best.Strength);

                results.Add(best);
            }
        }

        return results
            .DistinctBy(x => x.Time)
            .OrderBy(x => x.Time)
            .ToList();
    }

    public static TimeSpan FindNearestTarget(
        TimeSpan time,
        TimeSpan episode)
    {
        var n =
            Math.Round(time.TotalSeconds / episode.TotalSeconds);

        return TimeSpan.FromSeconds(
            n * episode.TotalSeconds);
    }

    private static bool IsBoundaryText(
        string a,
        string b)
    {
        var t = (a + " " + b).ToLowerInvariant();

        return t.Contains("previously on") ||
               t.Contains("to be continued") ||
               t.Contains("next time") ||
               t.Contains("episode");
    }

    public static (TimeSpan, TimeSpan)? DetectCredits(
        ILogger _logger,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration)
    {
        // Only search final 25% of media
        var minimumCreditStart =
            TimeSpan.FromSeconds(totalDuration.TotalSeconds * 0.75);

        for (int i = 0; i < cues.Count; i++)
        {
            if (cues[i].Start < minimumCreditStart)
                continue;

            if (!IsCreditText(cues[i].Text))
                continue;

            var start = cues[i].Start;
            var end = start;

            int matches = 0;

            for (int j = i; j < cues.Count; j++)
            {
                if (IsCreditText(cues[j].Text))
                {
                    matches++;
                    end = cues[j].End;
                }
                else
                {
                    // tolerate tiny gaps
                    if (j + 1 < cues.Count &&
                        cues[j + 1].Start - cues[j].End <
                        TimeSpan.FromSeconds(10))
                    {
                        continue;
                    }

                    break;
                }
            }

            // Require sustained credits
            if (matches >= 5 &&
                (end - start) > TimeSpan.FromSeconds(45))
            {
                _logger.LogInformation(
                    "[Prod] detected credits at {Start}-{End}",
                    start,
                    end);

                return (start, end);
            }
        }

        return null;
    }

    private static bool IsCreditText(string text)
    {
        text = text.ToLowerInvariant();

        return text.Contains("credits") ||
               text.Contains("cast") ||
               text.Contains("written by") ||
               text.Contains("directed by") ||
               text.Contains("produced by") ||
               text.Contains("starring") ||
               text.Contains("music by");
    }
}
