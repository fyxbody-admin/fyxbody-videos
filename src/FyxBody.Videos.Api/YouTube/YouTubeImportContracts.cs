namespace FyxBody.Videos.Api.YouTube;

public sealed record VideoImportInput(string Url, int[]? Heights = null);
public sealed record VideoImportCapabilities(bool Enabled, int[] AvailableHeights, int[] DefaultHeights, int MaxDurationSeconds, long MaxFileBytes, bool SupportsStatusFilters = true, bool SupportsJobSearch = true);
public sealed record VideoImportView(Guid Id, string YoutubeId, string SourceUrl, string? Title, string Status, string Stage,
    int ProgressPercent, Guid? VideoId, int[] RequestedHeights, int[] CompletedHeights, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? ErrorCode, string? ErrorMessage, string? Reference = null, string? ImportOrigin = null);

public sealed record VideoImportProgress(int Total, int Completed, int Failed, int CompletedLastHour, DateTimeOffset? EstimatedEnd, int CompletedLocalMp4, int CompletedVivaldi, int CompletedOther, int CompletedUntracked);
