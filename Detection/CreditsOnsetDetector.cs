using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection;

/// <summary>
/// Finds the exact frame where end-credits begin by detecting the last
/// significant scene change in the final portion of the film that is NOT
/// followed by resumed dialogue within a short window.
///
/// Works for black cards, white cards, coloured slates, hard cuts to
/// stylised credit text, and fade-to-any-colour transitions.
/// </summary>
public static class CreditsOnsetDetector
{
    // Only examine the last quarter of the film.
    private static readonly double SearchFromFraction = 0.75;

    // A scene-change score above this threshold counts as a hard cut or fade.
    // 0.35 catches most credit onsets; lower = more sensitive (more false positives).
    private static readonly double SceneChangeThreshold = 0.35;

    // If dialogue resumes within this window after a candidate cut, the cut
    // is mid-scene, not credits.
    private static readonly TimeSpan DialogueResumeWindow = TimeSpan.FromSeconds(30);

    public static async Task<TimeSpan?> DetectAsync(
        string ffmpegPath,
        string videoPath,
        TimeSpan totalDuration,
        IReadOnlyList<SubtitleCue> cues,
        ILogger logger,
        CancellationToken ct)
    {
        var searchStart = TimeSpan.FromSeconds(
            totalDuration.TotalSeconds * SearchFromFraction);

        // Ask ffmpeg to print the timestamp and scene-change score for every
        // frame that exceeds the threshold. -vsync 0 avoids duplicate frames.
        var args =
            $"-ss {searchStart:hh\\:mm\\:ss\\.fff} " +
            $"-i \"{videoPath}\" " +
            $"-vf \"select='gt(scene,{SceneChangeThreshold.ToString(CultureInfo.InvariantCulture)})',showinfo\" " +
            $"-vsync 0 -an -f null -";

        logger.LogInformation(
            "[Credits] scanning for scene changes from {Start}", searchStart);

        string stderr;
        try
        {
            stderr = await RunFfmpegStderrAsync(ffmpegPath, args, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Credits] ffmpeg scene-change scan failed — skipping");
            return null;
        }

        // showinfo prints lines containing:  pts_time:<seconds>
        // We collect every reported timestamp (relative to -ss) and convert to
        // absolute file positions.
        var cuts = ParseSceneChangeTimes(stderr, searchStart, logger);

        if (cuts.Count == 0)
        {
            logger.LogInformation("[Credits] no scene changes found in search window");
            return null;
        }

        logger.LogInformation(
            "[Credits] found {Count} scene-change candidates", cuts.Count);

        // Walk candidates from the END backwards. The last one that has no
        // subtitle cue starting within DialogueResumeWindow after it is the
        // credits onset — post-credits stingers will have dialogue after them,
        // so they are skipped.
        for (int i = cuts.Count - 1; i >= 0; i--)
        {
            var candidate = cuts[i];

            bool dialogueResumesAfter = cues.Any(c =>
                c.Start > candidate &&
                c.Start <= candidate + DialogueResumeWindow);

            if (dialogueResumesAfter)
            {
                logger.LogDebug(
                    "[Credits] skipping cut at {T} — dialogue resumes after it", candidate);
                continue;
            }

            logger.LogInformation(
                "[Credits] credits onset at {T}", candidate);

            return candidate;
        }

        logger.LogInformation(
            "[Credits] all candidates had dialogue after them — no credits boundary found");
        return null;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static List<TimeSpan> ParseSceneChangeTimes(
        string stderr,
        TimeSpan searchStart,
        ILogger logger)
    {
        var results = new List<TimeSpan>();

        // showinfo line format (among other fields):
        //   … pts_time:1234.567 …
        foreach (Match m in Regex.Matches(stderr, @"pts_time:([\d.]+)"))
        {
            if (!double.TryParse(
                    m.Groups[1].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var relSec))
                continue;

            var absolute = searchStart + TimeSpan.FromSeconds(relSec);
            results.Add(absolute);
        }

        results.Sort();
        return results;
    }

    // ── ffmpeg runner ─────────────────────────────────────────────────────────

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
            throw new Exception(
                $"ffmpeg exited {proc.ExitCode}:\n{stderr}");

        return stderr;
    }
}
