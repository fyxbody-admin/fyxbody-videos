namespace FyxBody.Videos.Data;

public sealed class VideoImportJob : Entity
{
    public string YoutubeId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string? Title { get; set; }
    public string? ImportOrigin { get; set; }
    public string? Reference { get; set; }
    public string? RequestedTitle { get; set; }
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "queued";
    public int ProgressPercent { get; set; }
    public Guid? VideoId { get; set; }
    public int[] RequestedHeights { get; set; } = [];
    public int[] CompletedHeights { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
