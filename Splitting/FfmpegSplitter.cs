using Microsoft.Extensions.Logging;

namespace MovieSplitter.Splitting;

public record EpisodeSegment(
    int Number,
    TimeSpan Start,
    TimeSpan End,
    string OutputPath);

public class FfmpegSplitter
{
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;

    public FfmpegSplitter(string ffmpegPath, ILogger logger)
    {
        _logger = logger;
        _ffmpegPath = FindFfmpeg(ffmpegPath);
    }

    private string FindFfmpeg(string ffmpegPath)
    {
        var candidates = new[]
        {
            ffmpegPath,
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/bin/ffmpeg",
            "ffmpeg"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogInformation("Using FFmpeg at: {Path}", candidate);
                return candidate;
            }
        }

        _logger.LogWarning("FFmpeg not found in common locations.");
        return "ffmpeg";
    }

    public async Task<IReadOnlyList<EpisodeSegment>> SplitAsync(
        string inputPath,
        IReadOnlyList<TimeSpan> startTimes,
        (TimeSpan Start, TimeSpan End)? credits,
        TimeSpan totalDuration,
        string[] outputDirs,
        string seriesName,
        CancellationToken ct)
    {
        if (outputDirs == null || outputDirs.Length == 0)
            throw new ArgumentException("At least one output directory is required", nameof(outputDirs));

        var extension = Path.GetExtension(inputPath);

        var points = new[] { TimeSpan.Zero }.Concat(startTimes).ToList();

        var segments = new List<EpisodeSegment>();

        var safeName = string.Concat(seriesName.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim(' ', '.', '_', '-');

        for (int i = 0; i < points.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var episodeStart = points[i];
            var episodeEnd = (i + 1 < points.Count)
                ? points[i + 1]
                : (credits?.Start ?? totalDuration);

            var episodeNum = i + 1;
            var fileName = $"{safeName} - S01E{episodeNum:D2}{extension}";

            var tempOutput = Path.Combine(Path.GetTempPath(), $"moviesplitter-{Guid.NewGuid():N}{extension}");

            _logger.LogInformation("Processing episode {Ep}: {Start} → {End} {Credits}",
                episodeNum, episodeStart, episodeEnd, credits.HasValue ? "+credits" : "");

            if (credits.HasValue && credits.Value.End > credits.Value.Start)
            {
                await CreateEpisodeWithCreditsAsync(inputPath, episodeStart, episodeEnd,
                    credits.Value.Start, credits.Value.End, tempOutput, ct);
            }
            else
            {
                var args = $"-ss {episodeStart:hh\\:mm\\:ss\\.fff} -to {episodeEnd:hh\\:mm\\:ss\\.fff} " +
                           $"-i \"{inputPath}\" -map 0 -c copy \"{tempOutput}\"";
                await RunFfmpegAsync(args, ct);
            }

            // Copy to output directories
            string? firstOutput = null;
            try
            {
                foreach (var outputDir in outputDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = Path.Combine(outputDir, safeName);
                    Directory.CreateDirectory(dir);
                    var outputPath = Path.Combine(dir, fileName);
                    File.Copy(tempOutput, outputPath, overwrite: true);
                    firstOutput ??= outputPath;
                }

                segments.Add(new EpisodeSegment(episodeNum, episodeStart, episodeEnd, firstOutput!));
            }
            finally
            {
                TryDelete(tempOutput);
            }
        }

        return segments;
    }

    private async Task CreateEpisodeWithCreditsAsync(
    string inputPath,
    TimeSpan epStart,
    TimeSpan epEnd,
    TimeSpan credStart,
    TimeSpan credEnd,
    string finalPath,
    CancellationToken ct)
    {
        var mainTs = Path.Combine(Path.GetTempPath(), $"main-{Guid.NewGuid():N}.ts");
        var credTs = Path.Combine(Path.GetTempPath(), $"cred-{Guid.NewGuid():N}.ts");

        try
        {
            // Main episode
            await RunFfmpegAsync(
                $"-y -ss {epStart:hh\\:mm\\:ss\\.fff} -to {epEnd:hh\\:mm\\:ss\\.fff} " +
                $"-i \"{inputPath}\" -map 0 -c copy -bsf:v h264_mp4toannexb -f mpegts \"{mainTs}\"", ct);

            // Credits
            await RunFfmpegAsync(
                $"-y -ss {credStart:hh\\:mm\\:ss\\.fff} -to {credEnd:hh\\:mm\\:ss\\.fff} " +
                $"-i \"{inputPath}\" -map 0 -c copy -bsf:v h264_mp4toannexb -f mpegts \"{credTs}\"", ct);

            // Concat — only apply aac_adtstoasc if audio is AAC
            var bsfAudio = IsAacAudio(inputPath) ? "-bsf:a aac_adtstoasc" : "";

            await RunFfmpegAsync(
                $"-y -i \"concat:{mainTs}|{credTs}\" -c copy {bsfAudio} \"{finalPath}\"", ct);
        }
        finally
        {
            TryDelete(mainTs);
            TryDelete(credTs);
        }
    }

    private bool IsAacAudio(string inputPath)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-i \"{inputPath}\" -hide_banner",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            proc.Start();
            var output = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            // Look for AAC in the audio stream (more reliable than codec name sometimes)
            return output.Contains("Audio: aac", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // safer to omit the filter than to crash
        }
    }

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        proc.Start();
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new Exception($"FFmpeg failed (exit {proc.ExitCode}):\n{stderr}");
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete {Path}", path); }
    }
}
