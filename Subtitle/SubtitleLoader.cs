using Microsoft.Extensions.Logging;

namespace MovieSplitter.Subtitle;

/// <summary>
/// Locates and loads subtitle content for a given movie file.
/// Priority: external .srt sidecar → external .ass sidecar → embedded extraction via ffmpeg.
/// </summary>
public class SubtitleLoader
{
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    public SubtitleLoader(PluginConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<string?> LoadAsync(string moviePath, CancellationToken ct)
    {
        var dir  = Path.GetDirectoryName(moviePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(moviePath);
        var lang = _config.SubtitleLanguage;

        // 1. External sidecar: Movie.eng.srt, Movie.srt, Movie.eng.ass, Movie.ass
        var candidates = new[]
        {
            Path.Combine(dir, $"{stem}.{lang}.srt"),
            Path.Combine(dir, $"{stem}.srt"),
            Path.Combine(dir, $"{stem}.{lang}.ass"),
            Path.Combine(dir, $"{stem}.ass"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            _logger.LogInformation("[SubtitleLoader] Found sidecar: {Path}", path);
            var content = await File.ReadAllTextAsync(path, ct);
            // Convert ASS to SRT-like plain text if needed
            return path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)
                ? AssToSrt(content)
                : content;
        }

        // 2. Extract embedded subtitle track via ffmpeg
        _logger.LogInformation("[SubtitleLoader] No sidecar found, attempting ffmpeg extraction");
        return await ExtractEmbeddedAsync(moviePath, ct);
    }

    // ── Embedded extraction ────────────────────────────────────────────────

    private async Task<string?> ExtractEmbeddedAsync(string moviePath, CancellationToken ct)
    {
        var tmpFile = Path.GetTempFileName() + ".srt";
        try
        {
            // ffmpeg -i input -map 0:s:0 -c:s srt output.srt
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName  = Plugin.Instance!.GetFfmpegPath(),
                Arguments = $"-i \"{moviePath}\" -map 0:s:0 -c:s srt \"{tmpFile}\" -y",
                RedirectStandardError = true,
                UseShellExecute = false
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || !File.Exists(tmpFile))
            {
                _logger.LogWarning("[SubtitleLoader] ffmpeg subtitle extraction failed");
                return null;
            }

            return await File.ReadAllTextAsync(tmpFile, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubtitleLoader] Exception during ffmpeg extraction");
            return null;
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    // ── ASS → plain text ───────────────────────────────────────────────────

    private static string AssToSrt(string assContent)
    {
        // Very lightweight: strip ASS override tags and reformat as SRT
        var lines   = assContent.Split('\n');
        var sb      = new System.Text.StringBuilder();
        int index   = 1;
        var tagRx   = new System.Text.RegularExpressions.Regex(@"\{[^}]*\}");
        var eventRx = new System.Text.RegularExpressions.Regex(
            @"Dialogue:\s*\d+,(\d+:\d+:\d+\.\d+),(\d+:\d+:\d+\.\d+),[^,]*,[^,]*,\d+,\d+,\d+,[^,]*,(.+)");

        foreach (var line in lines)
        {
            var m = eventRx.Match(line);
            if (!m.Success) continue;

            // ASS uses h:mm:ss.cs (centiseconds); convert to SRT hh:mm:ss,ms
            var start = AssTimeToSrt(m.Groups[1].Value);
            var end   = AssTimeToSrt(m.Groups[2].Value);
            var text  = tagRx.Replace(m.Groups[3].Value.Trim(), "").Replace("\\N", "\n");

            sb.AppendLine(index++.ToString());
            sb.AppendLine($"{start} --> {end}");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string AssTimeToSrt(string assTime)
    {
        // Input:  1:23:45.67   Output: 01:23:45,670
        var parts = assTime.Split(':');
        var secs  = parts[2].Split('.');
        return $"{int.Parse(parts[0]):D2}:{int.Parse(parts[1]):D2}:{int.Parse(secs[0]):D2},{int.Parse(secs[1]) * 10:D3}";
    }
}
