using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

public static class BoundaryDetectionHelper
{
    public static readonly TimeSpan BoundaryWindow =
        TimeSpan.FromMinutes(10);

    public static readonly TimeSpan CandidateGapThreshold =
        TimeSpan.FromSeconds(20);

    public static readonly TimeSpan StrongGapThreshold =
        TimeSpan.FromSeconds(60);

    public enum Confidence
    {
        Low,
        Medium,
        Strong,
        Chapter
    }

    public record Candidate(
        TimeSpan Time,
        Confidence Strength);

    public static List<Candidate> GenerateCandidates(
        ILogger logger,
        IChapterManager chapterManager,
        Movie item,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        TimeSpan targetEpisode,
        TimeSpan boundaryWindow,
        (TimeSpan, TimeSpan)? credits)
    {
        var chapterCandidates = FindChapterCandidates(
            logger,
            chapterManager,
            item,
            totalDuration,
            targetEpisode,
            boundaryWindow,
            credits);

        // Fast-path: chapters are usually authoritative
        if (chapterCandidates.Count > 0)
        {
            logger.LogInformation(
                "[Boundary] using chapter fast-path candidates={Count}",
                chapterCandidates.Count);

            return chapterCandidates;
        }

        return FindSubtitleCandidates(
            logger,
            cues,
            totalDuration,
            targetEpisode,
            boundaryWindow,
            credits);
    }

    public static List<Candidate> FindChapterCandidates(
        ILogger logger,
        IChapterManager chapterManager,
        Movie item,
        TimeSpan totalDuration,
        TimeSpan targetEpisode,
        TimeSpan boundaryWindow,
        (TimeSpan, TimeSpan)? credits)
    {
        var results = new List<Candidate>();

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

        logger.LogInformation(
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
                .OrderBy(c =>
                    Math.Abs((c - target).TotalSeconds))
                .FirstOrDefault();

            if (best == TimeSpan.Zero)
                continue;

            if (credits is not null &&
                best >= credits.Value.Item1)
            {
                continue;
            }

            logger.LogInformation(
                "[Chapters] selected target={Target} actual={Actual}",
                target,
                best);

            results.Add(
                new Candidate(
                    best,
                    Confidence.Chapter));
        }

        return results
            .DistinctBy(x => Math.Round(x.Time.TotalSeconds / 30))
            .OrderBy(x => x.Time)
            .ToList();
    }

    public static List<Candidate> FindSubtitleCandidates(
        ILogger logger,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        TimeSpan targetEpisode,
        TimeSpan boundaryWindow,
        (TimeSpan, TimeSpan)? credits)
    {
        var results = new List<Candidate>();

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

            logger.LogInformation(
                "[Subtitles] scanning target={Target} window={Start}-{End}",
                target,
                windowStart,
                windowEnd);

            Candidate? best = null;

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
                Confidence confidence = Confidence.Low;

                if (gap >= StrongGapThreshold)
                {
                    candidateTime = cur.End;
                    confidence = Confidence.Strong;
                }
                else if (gap >= CandidateGapThreshold)
                {
                    candidateTime = cur.End;
                    confidence = Confidence.Medium;
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
                logger.LogInformation(
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

    public static (TimeSpan Start, TimeSpan End)? DetectCredits(
        ILogger logger,
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration)
    {
        if (cues.Count == 0)
            return null;

        var last = cues[cues.Count - 1];

        var threshold =
            TimeSpan.FromSeconds(
                totalDuration.TotalSeconds * 0.90);

        if (last.Start < threshold)
            return null;

        logger.LogInformation(
            "[Credits] using final subtitle as boundary at {End}",
            last.End);

        return (last.End, totalDuration);
    }
}
