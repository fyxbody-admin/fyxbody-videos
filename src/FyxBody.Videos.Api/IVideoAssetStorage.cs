using FyxBody.Videos.Data;

namespace FyxBody.Videos.Api;

public interface IVideoAssetStorage
{
    Task UploadAsync(VideoAsset asset, Stream content, CancellationToken cancellationToken);
    Task<(long Bytes, int DurationMs)?> FindImportAsync(VideoAsset asset, CancellationToken cancellationToken) =>
        Task.FromResult<(long, int)?>(null);
    Task DeleteAsync(VideoAsset asset, CancellationToken cancellationToken);
}
