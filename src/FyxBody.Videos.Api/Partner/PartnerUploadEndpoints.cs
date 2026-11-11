using Microsoft.AspNetCore.Mvc;

namespace FyxBody.Videos.Api.Partner;

public sealed record PartnerPrivateVideoForm(IFormFile File, string Title, string Language, string Rendition, int DurationMs, string? PartnerReference);
public sealed record PrivateVideoMetadata(string Title, string? PartnerReference);
public sealed record PrivateVideoResponse(Guid Id, Guid LicenseId, string ExternalExerciseId, string Title, string? PartnerReference, AssetResponse[] Assets, string Status = "active");
public sealed record PrivateVideoUsage(Guid LicenseId, bool AllowPartnerUploads, string QuotaMode, long? QuotaLimit, long StorageBytes, int VideoCount, bool LicenseActive);

public static class PartnerUploadEndpoints
{
    public static IEndpointRouteBuilder MapPartnerUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization("partner").RequireRateLimiting("partner").WithGroupName("partner").WithTags("Private videos");
        group.MapGet("/licenses/{licenseId:guid}/private-videos", async (Guid licenseId, HttpContext context,
            PartnerVideoUploadService service, int? page, int? pageSize, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(PartnerIdentity.From(context.User), licenseId, page, pageSize, ct)));
        group.MapPost("/licenses/{licenseId:guid}/private-videos", async (Guid licenseId,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpRequest request, PartnerVideoUploadService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UploadAsync(PartnerIdentity.From(request.HttpContext.User), licenseId, idempotencyKey, request, ct)))
            .Accepts<PartnerPrivateVideoForm>("multipart/form-data")
            .WithSummary("Upload an MP4 owned by the authenticated partner")
            .ProducesProblem(409).ProducesProblem(413);
        group.MapGet("/licenses/{licenseId:guid}/private-video-usage", async (Guid licenseId, HttpContext context, PartnerVideoUploadService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UsageAsync(PartnerIdentity.From(context.User), licenseId, ct)));
        group.MapPatch("/private-videos/{videoId:guid}", async (Guid videoId, PrivateVideoMetadata metadata, HttpContext context, PartnerVideoUploadService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(PartnerIdentity.From(context.User), videoId, metadata, ct)));
        group.MapDelete("/private-videos/{videoId:guid}", async (Guid videoId, HttpContext context, PartnerVideoUploadService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(PartnerIdentity.From(context.User), videoId, ct);
            return TypedResults.NoContent();
        });
        return endpoints;
    }
}
