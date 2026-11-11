namespace FyxBody.Videos.Api.YouTube;

public sealed record MediaImportProgress(string Stage, int Percent);
public sealed record PreparedYouTubeRendition(int Height, string FilePath, long SizeBytes, int DurationMs);

public sealed class PreparedYouTubeVideo(string title, int sourceHeight, int durationMs,
    IReadOnlyList<PreparedYouTubeRendition> renditions, Func<ValueTask>? cleanup = null) : IAsyncDisposable
{
    public string Title { get; } = title;
    public int SourceHeight { get; } = sourceHeight;
    public int DurationMs { get; } = durationMs;
    public IReadOnlyList<PreparedYouTubeRendition> Renditions { get; } = renditions;
    public ValueTask DisposeAsync() => cleanup?.Invoke() ?? ValueTask.CompletedTask;
}

public interface IYouTubeMediaProcessor
{
    Task<PreparedYouTubeVideo> PrepareAsync(string youtubeId, IReadOnlyList<int> heights,
        Func<MediaImportProgress, Task> progress, CancellationToken cancellationToken);
}
