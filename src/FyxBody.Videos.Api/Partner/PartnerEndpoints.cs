namespace FyxBody.Videos.Api.Partner;

public sealed record PlaybackRequest(Guid VideoAssetId);
public sealed record PlaybackSessionResponse(Guid SessionId, Guid VideoId, Guid VideoAssetId, DateTimeOffset ExpiresAt, Uri Url, bool Replayed);
public sealed record PlaybackEventRequest(Guid SessionId, string EventKey, string Kind, int PositionMs, DateTimeOffset OccurredAt);
public sealed record EventBatchRequest(PlaybackEventRequest[] Events);
public sealed record EventBatchResponse(int Accepted, int Duplicates);
public sealed record AssetResponse(Guid Id, string Language, string Rendition, string ContentType, long SizeBytes, int DurationMs);
public sealed record VideoResponse(Guid Id, string ExternalExerciseId, string Title, AssetResponse[] Assets, string? PartnerReference = null);
public sealed record CatalogResponse(int Page, int PageSize, int Total, VideoResponse[] Items);
public sealed record LicenseUsageResponse(Guid LicenseId, long MonthlySessionQuota, long SessionsIssued, string? LicenseName = null, bool UnlimitedSessions = false);
public sealed record UsageResponse(string Month, LicenseUsageResponse[] Licenses, int StartedSessions, int CompletedSessions);

public sealed record PartnerManagementView(bool CanManageSubpartners, bool CanConfigureSubpartnerUploads, Admin.LicenseView[] Licenses);

public static class PartnerEndpoints
{
    public static IEndpointRouteBuilder MapPartnerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization("partner").RequireRateLimiting("partner").WithGroupName("partner").WithTags("Partner videos");
        group.MapGet("/management", async (HttpContext context, PartnerVideoService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ManagementAsync(PartnerIdentity.From(context.User), ct)));
        group.MapGet("/licenses/{licenseId:guid}/delegable-videos", async (Guid licenseId, HttpContext context,
            PartnerVideoService service, int? page, int? pageSize, CancellationToken ct) =>
            TypedResults.Ok(await service.DelegableVideosAsync(PartnerIdentity.From(context.User), licenseId, page, pageSize, ct)));
        group.MapGet("/videos", async (HttpContext context, PartnerVideoService service, int? page, int? pageSize, string? externalExerciseId, CancellationToken ct) =>
            TypedResults.Ok(await service.CatalogAsync(PartnerIdentity.From(context.User), page ?? 1, pageSize ?? 25, ct, externalExerciseId)));
        group.MapGet("/videos/{videoId:guid}", async (Guid videoId, HttpContext context, PartnerVideoService service, CancellationToken ct) =>
            TypedResults.Ok(await service.VideoAsync(PartnerIdentity.From(context.User), videoId, ct)));
        group.MapPost("/videos/{videoId:guid}/playback-sessions", async (Guid videoId, PlaybackRequest request, [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpContext context, PartnerVideoService service, CancellationToken ct) =>
        {
            var response = await service.IssueAsync(PartnerIdentity.From(context.User), videoId, request.VideoAssetId, idempotencyKey ?? "", ct);
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(response);
        });
        group.MapPost("/playback-events", async (EventBatchRequest request, HttpContext context, PartnerVideoService service, CancellationToken ct) =>
            TypedResults.Ok(await service.EventsAsync(PartnerIdentity.From(context.User), request.Events, ct)));
        group.MapGet("/usage", async (string? month, HttpContext context, PartnerVideoService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UsageAsync(PartnerIdentity.From(context.User), month, ct)));
        return endpoints;
    }
}
