using MediaBrowser.Model.Plugins;
using MovieSplitter.Detection;

namespace MovieSplitter;

public class PluginConfiguration : BasePluginConfiguration
{
    // ── Heuristic settings ────────────────────────────────────────────────────
    public double SilenceThresholdSeconds { get; set; } = 30.0;
    public double MinEpisodeMinutes       { get; set; } = 10.0;
    public string CueWordPatterns         { get; set; } =
        @"previously on,next time on,\bchapter \d+\b,\bpart \d+\b";

    // ── Ollama settings ───────────────────────────────────────────────────────
    public bool   OllamaEnabled  { get; set; } = false;
    public string OllamaUrl      { get; set; } = "http://localhost:11434";
    public string OllamaModel    { get; set; } = "llama3";

    // ── Detector selection ────────────────────────────────────────────────────
    public DetectorMode DetectorMode { get; set; } = DetectorMode.Heuristic;

    // ── Output settings ───────────────────────────────────────────────────────
    public string OutputSubfolder  { get; set; } = "Episodes";
    public string SubtitleLanguage { get; set; } = "eng";
}
