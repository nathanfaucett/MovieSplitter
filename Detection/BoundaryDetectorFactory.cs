using Microsoft.Extensions.Logging;
using MovieSplitter.Detection.Ollama;

namespace MovieSplitter.Detection;

public static class BoundaryDetectorFactory
{
    public static IBoundaryDetector Create(
        PluginConfiguration config,
        ILogger logger)
    {
        var heuristic = new HeuristicBoundaryDetector(config, logger);

        if (!config.OllamaEnabled || string.IsNullOrWhiteSpace(config.OllamaUrl))
        {
            logger.LogInformation("[Factory] Ollama not configured → using Heuristic detector");
            return heuristic;
        }

        var ollamaClient   = new OllamaClient(config.OllamaUrl, config.OllamaModel, logger);
        var ollamaDetector = new OllamaBoundaryDetector(config, ollamaClient, logger);

        return config.DetectorMode switch
        {
            DetectorMode.Ollama => ollamaDetector,

            DetectorMode.Composite => new CompositeBoundaryDetector(
                new IBoundaryDetector[] { heuristic, ollamaDetector },
                config, logger),

            _ => heuristic
        };
    }
}
