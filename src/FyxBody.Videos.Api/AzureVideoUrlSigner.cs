using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using FyxBody.Videos.Data;
using System.Text.RegularExpressions;
namespace FyxBody.Videos.Api;

public sealed class AzureVideoUrlSigner(IConfiguration configuration, TimeProvider clock, IHostEnvironment? environment = null) : IVideoUrlSigner, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private UserDelegationKey? key;
    private BlobServiceClient? client;

    public async Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var account = configuration["Storage:AccountName"] ?? "";
        var allowed = configuration.GetSection("Storage:AllowedContainers").Get<string[]>() ?? ["videos"];
        if (!Regex.IsMatch(account, "^[a-z0-9]{3,24}$") || !allowed.Contains(asset.BlobContainer, StringComparer.Ordinal))
            throw new ApiException(503, "video_storage_unavailable", "Video storage is not configured for this asset.");
        var now = clock.GetUtcNow();
        if (expiresAt <= now || expiresAt > now.AddMinutes(15))
            throw new ApiException(409, "session_expired", "The playback permission has expired.");
        var options = new BlobClientOptions();
        options.Diagnostics.IsLoggingEnabled = false;
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        client ??= new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), StorageCredentialFactory.Create(configuration, environment), options);
        var blob = client.GetBlobContainerClient(asset.BlobContainer).GetBlobClient(asset.BlobName);
        try
        {
            // Ready metadata alone must not authorize a missing or mismatched object.
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (properties.Value.ContentLength != asset.SizeBytes || properties.Value.ContentType != "video/mp4")
                throw new ApiException(503, "video_unavailable", "The video file is not ready for playback.");
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (key is null || key.SignedExpiresOn < expiresAt.AddMinutes(2))
                    key = (await client.GetUserDelegationKeyAsync(new BlobGetUserDelegationKeyOptions(now.AddHours(1)) { StartsOn = now.AddMinutes(-5) }, cancellationToken)).Value;
            }
            finally { gate.Release(); }
            var builder = new BlobSasBuilder
            {
                BlobContainerName = asset.BlobContainer, BlobName = asset.BlobName, Resource = "b",
                Protocol = SasProtocol.Https, StartsOn = now.AddMinutes(-2), ExpiresOn = expiresAt
            };
            builder.SetPermissions(BlobSasPermissions.Read);
            return new BlobUriBuilder(blob.Uri) { Sas = builder.ToSasQueryParameters(key, account) }.ToUri();
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException)
        {
            throw new ApiException(503, "video_storage_unavailable", "Video storage is temporarily unavailable.");
        }
    }

    public void Dispose() => gate.Dispose();
}
