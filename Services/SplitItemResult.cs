namespace MovieSplitter.Services;

public enum SplitResultStatus
{
  Success,
  NotFound,
  BadRequest
}

public sealed record SplitItemResult(
    SplitResultStatus Status,
    int EpisodesCreated,
    string? Message);
