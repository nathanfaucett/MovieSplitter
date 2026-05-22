using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace MovieSplitter.Services;

public class LibraryScanService : ILibraryScanService
{
    private readonly ITaskManager _taskManager;
    private readonly ILogger<LibraryScanService> _logger;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(30);

    public LibraryScanService(
        ITaskManager taskManager,
        ILogger<LibraryScanService> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    public async Task QueueScanAndWaitAsync(CancellationToken ct = default)
    {
        var scanTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t =>
                string.Equals(t.Name, "Scan Media Library", StringComparison.OrdinalIgnoreCase));

        if (scanTask is null)
            throw new InvalidOperationException("Could not find Scan Media Library task");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
        {
            if (e.Task.Id != scanTask.Id)
                return;

            _logger.LogInformation("[Metadata] library scan completed with status {Status}", e.Result.Status);
            tcs.TrySetResult(true);
        }

        _taskManager.TaskCompleted += OnTaskCompleted;

        try
        {
            _logger.LogInformation("[Metadata] queueing library scan task {TaskId}", scanTask.Id);

            var method = typeof(ITaskManager)
                .GetMethods()
                .First(m => m.Name == "QueueScheduledTask" && m.IsGenericMethod);

            var generic = method.MakeGenericMethod(scanTask.ScheduledTask.GetType());
            generic.Invoke(_taskManager, new object[] { new TaskOptions() });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ScanTimeout);

            await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("[Metadata] library scan timed out after {Timeout}", ScanTimeout);
        }
        finally
        {
            _taskManager.TaskCompleted -= OnTaskCompleted;
        }
    }
}
