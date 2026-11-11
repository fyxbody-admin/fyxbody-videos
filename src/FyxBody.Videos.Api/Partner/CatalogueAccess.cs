using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;

namespace FyxBody.Videos.Api.Partner;

internal static class CatalogueAccess
{
    internal static IQueryable<License> ActiveLicenses(VideosDbContext db, Guid partnerId, DateTimeOffset now) =>
        db.Licenses.AsNoTracking().Where(license => license.PartnerId == partnerId && license.Status == "active"
            && license.StartsAt <= now && license.EndsAt > now);

    internal static IQueryable<Video> ReadyVideos(VideosDbContext db) =>
        db.Videos.AsNoTracking().Where(video => video.Status == "active" && db.VideoAssets.Any(asset =>
            asset.VideoId == video.Id && asset.Status == "ready" && asset.SizeBytes > 0 && asset.DurationMs > 0));

    internal static IQueryable<Video> DelegableVideos(VideosDbContext db, Guid partnerId, Guid sourceLicenseId) =>
        ReadyVideos(db).Where(video => (video.OwnerPartnerId == null || video.OwnerPartnerId == partnerId) &&
            db.LicenseVideos.Any(grant => grant.LicenseId == sourceLicenseId && grant.VideoId == video.Id
                && grant.SourceLicenseId == null && grant.RevokedAt == null));

    internal static IQueryable<LicenseVideo> EffectiveGrants(VideosDbContext db, Guid partnerId, DateTimeOffset now) =>
        from grant in db.LicenseVideos.AsNoTracking()
        join license in ActiveLicenses(db, partnerId, now) on grant.LicenseId equals license.Id
        join video in db.Videos.AsNoTracking() on grant.VideoId equals video.Id
        join partner in db.Partners.AsNoTracking() on license.PartnerId equals partner.Id
        where grant.RevokedAt == null && partner.Status == "active"
        where grant.SourceLicenseId == null
            ? (video.OwnerPartnerId == partnerId || (video.OwnerPartnerId == null && partner.ParentPartnerId == null))
            : partner.ParentPartnerId != null && db.Licenses.Any(source =>
                source.Id == grant.SourceLicenseId && source.PartnerId == partner.ParentPartnerId
                && source.AllowCatalogueDelegation && source.Status == "active"
                && source.StartsAt <= now && source.EndsAt > now
                && db.Partners.Any(parent => parent.Id == source.PartnerId && parent.Status == "active" && parent.ParentPartnerId == null)
                && (video.OwnerPartnerId == null || video.OwnerPartnerId == source.PartnerId)
                && db.LicenseVideos.Any(upstream => upstream.LicenseId == source.Id && upstream.VideoId == grant.VideoId
                    && upstream.SourceLicenseId == null && upstream.RevokedAt == null))
        select grant;

    internal static IQueryable<Video> VisibleVideos(VideosDbContext db, Guid partnerId, DateTimeOffset now) =>
        ReadyVideos(db).Where(video => EffectiveGrants(db, partnerId, now).Any(grant => grant.VideoId == video.Id));
}
