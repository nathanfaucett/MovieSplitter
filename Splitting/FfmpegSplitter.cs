using Microsoft.Extensions.Logging;

namespace MovieSplitter.Splitting;

public record EpisodeSegment(int Number, TimeSpan Start, TimeSpan End, string OutputPath);

public class FfmpegSplitter
{
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;

    public FfmpegSplitter(string ffmpegPath, ILogger logger)
    {
        _ffmpegPath = ffmpegPath;
        _logger     = logger;
    }

    public async Task<IReadOnlyList<EpisodeSegment>> SplitAsync(
        string inputPath,
        IReadOnlyList<TimeSpan> boundaries,
        TimeSpan totalDuration,
        string outputDir,
        string seriesName,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        var points = new[] { TimeSpan.Zero }
            .Concat(boundaries)
            .Append(totalDuration)
            .ToList();

        var segments = new List<EpisodeSegment>();

        for (int i = 0; i < points.Count - 1; i++)
        {
            var start      = points[i];
            var end        = points[i + 1];
            var episodeNum = i + 1;
            var safeName   = string.Concat(seriesName.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var outFile    = Path.Combine(outputDir,
                $"{safeName} - S01E{episodeNum:D2}.mkv");

            var args = string.Join(" ",
                $"-ss {start:hh\\:mm\\:ss\\.fff}",
                $"-to {end:hh\\:mm\\:ss\\.fff}",
                $"-i \"{inputPath}\"",
                "-c copy",
                "-map 0",
                $"\"{outFile}\"");

            _logger.LogInformation(
                "Splitting episode {Ep}: {Start} → {End}", episodeNum, start, end);

            await RunFfmpegAsync(args, ct);
            segments.Add(new EpisodeSegment(episodeNum, start, end, outFile));
        }

        return segments;
    }

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName              = _ffmpegPath,
            Arguments             = args,
            RedirectStandardError = true,
            UseShellExecute       = false
        };
        proc.Start();
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new Exception($"FFmpeg failed (exit {proc.ExitCode}): {err}");
        }
    }
}
