namespace MovieSplitter.Detection;

public enum DetectorMode
{
    Heuristic,   // subtitle gaps + cue words only
    Ollama,      // LLM only (falls back to heuristic on failure)
    Composite    // both, results merged
}
