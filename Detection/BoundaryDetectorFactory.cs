using MediaBrowser.Controller.Chapters;
using Microsoft.Extensions.Logging;
using MovieSplitter.Detection.Ollama;

namespace MovieSplitter.Detection;

public static class BoundaryDetectorFactory
{
    public static IBoundaryDetector Create(
        PluginConfiguration config,
        ILogger logger,
        IChapterManager chapterManager)
    {
        var boundaryDetectionService = new BoundaryDetectionService(logger);

        var heuristic = new HeuristicBoundaryDetector(
            config,
            logger,
            chapterManager,
            boundaryDetectionService);

        if (string.IsNullOrWhiteSpace(config.OllamaUrl))
        {
            logger.LogInformation("[Factory] Ollama not configured → using Heuristic detector");
            return heuristic;
        }

        return config.DetectorMode switch
        {
            DetectorMode.Ollama => new OllamaBoundaryDetector(
                config,
                new OllamaClient(config.OllamaUrl, config.OllamaModel, logger),
                logger,
                chapterManager,
                boundaryDetectionService),

            _ => heuristic
        };
    }
}
