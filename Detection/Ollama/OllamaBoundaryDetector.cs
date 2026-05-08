using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MovieSplitter.Subtitle;

namespace MovieSplitter.Detection.Ollama;

public class OllamaBoundaryDetector : IBoundaryDetector
{
    public string Name => "Ollama LLM";

    // How many subtitle cues to send per LLM chunk (keeps prompt manageable)
    private const int ChunkSize = 120;

    private static readonly TimeSpan BoundaryWindow =
        TimeSpan.FromMinutes(10);

    private static readonly Regex TimestampRx = new(
        @"\b(\d{1,2}):(\d{2}):(\d{2})(?:[.,](\d{1,3}))?\b",
        RegexOptions.Compiled);

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

    public async Task<IReadOnlyList<TimeSpan>> DetectAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct = default)
    {
        if (cues.Count == 0) return Array.Empty<TimeSpan>();

        try
        {
            var boundaries = await RunLlmDetectionAsync(cues, totalDuration, ct);
            _logger.LogInformation("[Ollama] Detected {N} boundaries via LLM", boundaries.Count);
            return boundaries;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or InvalidOperationException)
        {
            _logger.LogWarning(ex,
                "[Ollama] LLM call failed, falling back to heuristic detector");
            return await _fallback.DetectAsync(cues, totalDuration, ct);
        }
    }

    private async Task<IReadOnlyList<TimeSpan>> RunLlmDetectionAsync(
        IReadOnlyList<SubtitleCue> cues,
        TimeSpan totalDuration,
        CancellationToken ct)
    {
        var allBoundaries = new HashSet<TimeSpan>();
        int overlap = ChunkSize / 4;

        for (int start = 0; start < cues.Count; start += ChunkSize - overlap)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = cues.Skip(start).Take(ChunkSize).ToList();
            var prompt = BuildPrompt(chunk, totalDuration);
            var response = await _client.GenerateAsync(prompt, ct);
            var found = ParseTimestamps(response, chunk);
            foreach (var ts in found) allBoundaries.Add(ts);
        }

        return FilterAndSort(allBoundaries, totalDuration);
    }

    private static string BuildPrompt(
        IReadOnlyList<SubtitleCue> chunk, TimeSpan totalDuration)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing subtitle data from a movie that contains multiple episodes joined into a single file.");
        sb.AppendLine($"Total duration: {totalDuration:hh\\:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("Below is a window of subtitle cues in the format [HH:MM:SS.mmm --> HH:MM:SS.mmm] TEXT.");
        sb.AppendLine("Identify timestamps where one episode ENDS and another BEGINS.");
        sb.AppendLine("Look for: long gaps with no dialogue, phrases like 'Previously on', 'Next time', chapter titles, credits followed by new content.");
        sb.AppendLine();
        sb.AppendLine("Return ONLY a JSON array of timestamps in HH:MM:SS format where splits should occur.");
        sb.AppendLine("Example output: [\"00:42:15\", \"01:24:30\"]");
        sb.AppendLine("If no episode boundary is found in this window, return an empty array: []");
        sb.AppendLine();
        sb.AppendLine("SUBTITLE WINDOW:");

        foreach (var cue in chunk)
            sb.AppendLine($"[{cue.Start:hh\\:mm\\:ss\\.fff} --> {cue.End:hh\\:mm\\:ss\\.fff}] {cue.Text}");

        sb.AppendLine();
        sb.Append("OUTPUT (JSON array only, no explanation):");
        return sb.ToString();
    }

    private IReadOnlyList<TimeSpan> ParseTimestamps(
        string llmResponse, IReadOnlyList<SubtitleCue> chunk)
    {
        var results = new List<TimeSpan>();

        try
        {
            var clean = llmResponse.Trim().TrimStart('`').TrimEnd('`');
            if (clean.StartsWith('['))
            {
                var strings = System.Text.Json.JsonSerializer
                    .Deserialize<List<string>>(clean) ?? new();

                foreach (var s in strings)
                    if (TryParseTimestamp(s, out var ts))
                        results.Add(ts);

                if (results.Count > 0) return results;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            _logger.LogDebug("[Ollama] JSON parse failed, falling back to regex extraction");
        }

        foreach (Match m in TimestampRx.Matches(llmResponse))
        {
            if (TryParseTimestamp(m.Value, out var ts))
                results.Add(ts);
        }

        var chunkStart = chunk.First().Start;
        var chunkEnd = chunk.Last().End;
        return results.Where(t => t >= chunkStart && t <= chunkEnd).ToList();
    }

    private static bool TryParseTimestamp(string s, out TimeSpan result)
    {
        s = s.Replace(',', '.');
        if (TimeSpan.TryParseExact(s,
                new[] { @"hh\:mm\:ss", @"hh\:mm\:ss\.fff", @"h\:mm\:ss", @"h\:mm\:ss\.fff" },
                null, out result))
            return true;

        result = default;
        return false;
    }

    private IReadOnlyList<TimeSpan> FilterAndSort(
    HashSet<TimeSpan> candidates,
    TimeSpan totalDuration)
    {
        if (candidates.Count == 0)
            return [];

        var targetEpisode =
            TimeSpan.FromMinutes(_config.TargetEpisodeMinutes);

        var sorted = candidates
            .OrderBy(t => t)
            .ToList();

        var result = new List<TimeSpan>();

        var currentStart = TimeSpan.Zero;

        while (true)
        {
            var targetBoundary = currentStart + targetEpisode;

            var nearby = sorted
                .Where(t =>
                    t > currentStart &&
                    t >= targetBoundary - BoundaryWindow &&
                    t <= targetBoundary + BoundaryWindow)
                .ToList();

            // fallback: nearest future timestamps
            if (nearby.Count == 0)
            {
                nearby = sorted
                    .Where(t => t > currentStart)
                    .Take(5)
                    .ToList();
            }

            if (nearby.Count == 0)
                break;

            // Choose timestamp closest to ideal duration
            var best = nearby
                .OrderBy(t =>
                    Math.Abs((t - targetBoundary).TotalSeconds))
                .First();

            // Avoid tiny tail episode
            if (totalDuration - best < targetEpisode / 2)
                break;

            result.Add(best);

            _logger.LogDebug(
                "[Ollama] Selected boundary at {Time:g}",
                best);

            currentStart = best;
        }

        return result;
    }
}
