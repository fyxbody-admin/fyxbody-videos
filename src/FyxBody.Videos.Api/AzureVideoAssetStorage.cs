using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FyxBody.Videos.Data;
using System.Text.RegularExpressions;

namespace FyxBody.Videos.Api;

public sealed class AzureVideoAssetStorage(IConfiguration configuration, IHostEnvironment? environment = null) : IVideoAssetStorage
{
    private BlobClient Blob(VideoAsset asset)
    {
        var account = configuration["Storage:AccountName"] ?? "";
        var allowed = configuration.GetSection("Storage:AllowedContainers").Get<string[]>() ?? ["videos"];
        if (!Regex.IsMatch(account, @"\A[a-z0-9]{3,24}\z") || !allowed.Contains(asset.BlobContainer, StringComparer.Ordinal))
            throw new ApiException(503, "video_storage_unavailable", "Video storage is not configured for this asset.");
        var options = new BlobClientOptions();
        options.Diagnostics.IsLoggingEnabled = false;
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        return new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), StorageCredentialFactory.Create(configuration, environment), options)
            .GetBlobContainerClient(asset.BlobContainer).GetBlobClient(asset.BlobName);
    }

    public async Task UploadAsync(VideoAsset asset, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            await Blob(asset).UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = asset.BlobName.EndsWith("/import.mp4", StringComparison.Ordinal)
                    ? new Dictionary<string, string> { ["duration_ms"] = asset.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    : null,
                HttpHeaders = new BlobHttpHeaders { ContentType = "video/mp4" },
                TransferOptions = new StorageTransferOptions { MaximumConcurrency = 2, InitialTransferSize = 4 * 1024 * 1024, MaximumTransferSize = 4 * 1024 * 1024 }
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            throw new ApiException(409, "resource_conflict", "The generated storage object already exists; retry the upload.");
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException)
        {
            throw new ApiException(503, "video_storage_unavailable", "Video storage is temporarily unavailable.");
        }
    }

    public async Task<(long Bytes, int DurationMs)?> FindImportAsync(VideoAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            var properties = (await Blob(asset).GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            if (properties.ContentType != "video/mp4" || properties.ContentLength <= 0 || properties.ContentLength > YouTube.YouTubeMediaProcessor.FileLimit(configuration)
                || !properties.Metadata.TryGetValue("duration_ms", out var value)
                || !int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var duration) || duration <= 0)
                throw new ApiException(409, "import_asset_conflict", "The existing import file has invalid metadata.");
            return (properties.ContentLength, duration);
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound") { return null; }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException)
        { throw new ApiException(503, "video_storage_unavailable", "Video storage is temporarily unavailable."); }
    }

    public async Task DeleteAsync(VideoAsset asset, CancellationToken cancellationToken)
    {
        try { await Blob(asset).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken); }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException)
        { throw new ApiException(503, "video_storage_unavailable", "Video storage cleanup is temporarily unavailable."); }
    }
}
