namespace MovieSplitter.Services;

public interface ILibraryScanService
{
  Task QueueScanAndWaitAsync(CancellationToken ct = default);
}
