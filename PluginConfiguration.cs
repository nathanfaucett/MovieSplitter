using MediaBrowser.Model.Plugins;
using MovieSplitter.Detection;

namespace MovieSplitter;

public class PluginConfiguration : BasePluginConfiguration
{
    // ── Heuristic settings ────────────────────────────────────────────────────
    public double SilenceThresholdSeconds { get; set; } = 60.0;
    public double TargetEpisodeMinutes { get; set; } = 30.0;

    // ── Ollama settings ───────────────────────────────────────────────────────
    public string OllamaUrl { get; set; } = "http://host.docker.internal:11434";
    public string OllamaModel { get; set; } = "gemma4:e2b";

    // ── Detector selection ────────────────────────────────────────────────────
    public DetectorMode DetectorMode { get; set; } = DetectorMode.Heuristic;

    // ── Output settings ───────────────────────────────────────────────────────
    public string OutputSubfolder { get; set; } = "Episodes";
    public string SubtitleLanguage { get; set; } = "en";
}
