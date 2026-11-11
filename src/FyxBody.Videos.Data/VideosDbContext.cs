using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace FyxBody.Videos.Data;

public sealed class VideosDbContext(DbContextOptions<VideosDbContext> options) : DbContext(options)
{
    public DbSet<PortalUser> PortalUsers => Set<PortalUser>();
    public DbSet<VideoImportJob> VideoImportJobs => Set<VideoImportJob>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<VideoAsset> VideoAssets => Set<VideoAsset>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<PartnerCredential> PartnerCredentials => Set<PartnerCredential>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<LicenseVideo> LicenseVideos => Set<LicenseVideo>();
    public DbSet<PlaybackSession> PlaybackSessions => Set<PlaybackSession>();
    public DbSet<PlaybackEvent> PlaybackEvents => Set<PlaybackEvent>();
    public DbSet<UsageMonthly> UsageMonthly => Set<UsageMonthly>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("videos");

        var video = model.Entity<Video>();
        video.ToTable("videos", t => {
            t.HasCheckConstraint("ck_videos_status", "status IN ('draft','active','archived')");
            t.HasCheckConstraint("ck_videos_dates", "updated_at >= created_at");
            t.HasCheckConstraint("ck_videos_identity", "length(btrim(external_exercise_id)) > 0 AND length(btrim(title)) > 0");
        });
        video.Property(x => x.ExternalExerciseId).HasMaxLength(128).IsRequired();
        video.Property(x => x.Title).HasMaxLength(300).IsRequired();
        video.Property(x => x.Status).HasMaxLength(20).IsRequired();
        video.HasIndex(x => x.ExternalExerciseId).IsUnique();
        video.Property(x => x.PartnerReference).HasMaxLength(128);
        video.Property(x => x.UploadIdempotencyKey).HasMaxLength(128);
        video.Property(x => x.UploadRequestHash).HasMaxLength(64);
        video.HasIndex(x => new { x.OwnerPartnerId, x.UploadIdempotencyKey }).IsUnique();
        video.HasOne<Partner>().WithMany().HasForeignKey(x => x.OwnerPartnerId).OnDelete(DeleteBehavior.Restrict);
        video.HasOne<License>().WithMany().HasForeignKey(x => new { x.UploadLicenseId, x.OwnerPartnerId })
            .HasPrincipalKey(x => new { x.Id, x.PartnerId }).OnDelete(DeleteBehavior.Restrict);

        var asset = model.Entity<VideoAsset>();
        asset.ToTable("video_assets", t => {
            t.HasCheckConstraint("ck_video_assets_status", "status IN ('pending','ready','disabled')");
            t.HasCheckConstraint("ck_video_assets_sizes", "size_bytes >= 0 AND duration_ms >= 0");
            t.HasCheckConstraint("ck_video_assets_blob", "length(btrim(blob_container)) > 0 AND length(btrim(blob_name)) > 0");
            t.HasCheckConstraint("ck_video_assets_language", "language ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$' AND length(btrim(rendition)) > 0");
        });
        asset.Property(x => x.Language).HasMaxLength(35).IsRequired();
        asset.Property(x => x.Rendition).HasMaxLength(50).IsRequired();
        asset.Property(x => x.BlobContainer).HasMaxLength(63).IsRequired();
        asset.Property(x => x.BlobName).HasMaxLength(1024).IsRequired();
        asset.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        asset.Property(x => x.Status).HasMaxLength(20).IsRequired();
        asset.HasIndex(x => new { x.VideoId, x.Language, x.Rendition }).IsUnique();
        asset.HasIndex(x => new { x.BlobContainer, x.BlobName }).IsUnique();
        asset.HasAlternateKey(x => new { x.Id, x.VideoId });
        asset.HasOne<Video>().WithMany().HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.Restrict);

        var partner = model.Entity<Partner>();
        partner.ToTable("partners", t => {
            t.HasCheckConstraint("ck_partners_status", "status IN ('active','suspended','revoked')");
            t.HasCheckConstraint("ck_partners_code", "code ~ '^[a-z0-9][a-z0-9_-]*$' AND length(btrim(name)) > 0");
        });
        partner.Property(x => x.Code).HasMaxLength(64).IsRequired();
        partner.Property(x => x.Name).HasMaxLength(200).IsRequired();
        partner.Property(x => x.Status).HasMaxLength(20).IsRequired();
        partner.HasIndex(x => x.Code).IsUnique();
        partner.HasOne<Partner>().WithMany().HasForeignKey(x => x.ParentPartnerId).OnDelete(DeleteBehavior.Restrict);

        var credential = model.Entity<PartnerCredential>();
        credential.ToTable("partner_credentials", t => {
            t.HasCheckConstraint("ck_partner_credentials_dates", "(expires_at IS NULL OR expires_at > created_at) AND (revoked_at IS NULL OR revoked_at >= created_at)");
            t.HasCheckConstraint("ck_partner_credentials_hash", "length(btrim(secret_hash)) >= 32 AND length(btrim(key_id)) > 0");
        });
        credential.Property(x => x.KeyId).HasMaxLength(128).IsRequired();
        credential.Property(x => x.SecretHash).HasMaxLength(512).IsRequired();
        credential.HasIndex(x => x.KeyId).IsUnique();
        credential.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);

        var license = model.Entity<License>();
        license.ToTable("licenses", t => {
            t.HasCheckConstraint("ck_licenses_status", "status IN ('active','suspended','revoked')");
            t.HasCheckConstraint("ck_licenses_dates", "ends_at > starts_at");
            t.HasCheckConstraint("ck_licenses_quota", "monthly_session_quota >= 0");
            t.HasCheckConstraint("ck_licenses_upload_quota", "(upload_quota_mode = 'unlimited' AND upload_quota_limit IS NULL) OR (upload_quota_mode IN ('storage','videos') AND upload_quota_limit IS NOT NULL AND upload_quota_limit > 0)");
            t.HasCheckConstraint("ck_licenses_name", "length(btrim(name)) > 0");
        });
        license.Property(x => x.Name).HasMaxLength(200).IsRequired();
        license.Property(x => x.Status).HasMaxLength(20).IsRequired();
        license.Property(x => x.UnlimitedSessions).HasDefaultValue(false);
        license.Property(x => x.AllowSubpartnerManagement).HasDefaultValue(false);
        license.Property(x => x.AllowCatalogueDelegation).HasDefaultValue(false);
        license.Property(x => x.AllowPartnerUploads).HasDefaultValue(false);
        license.Property(x => x.UploadQuotaMode).HasMaxLength(16).HasDefaultValue("unlimited");
        license.HasAlternateKey(x => new { x.Id, x.PartnerId });
        license.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        license.HasIndex(x => new { x.PartnerId, x.Status, x.StartsAt });

        var licensedVideo = model.Entity<LicenseVideo>();
        licensedVideo.ToTable("license_videos", t => t.HasCheckConstraint("ck_license_videos_revoked", "revoked_at IS NULL OR revoked_at >= created_at"));
        licensedVideo.HasKey(x => new { x.LicenseId, x.VideoId });
        licensedVideo.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        licensedVideo.HasOne<License>().WithMany().HasForeignKey(x => x.SourceLicenseId).OnDelete(DeleteBehavior.Restrict);
        licensedVideo.HasOne<Video>().WithMany().HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.Restrict);

        var session = model.Entity<PlaybackSession>();
        session.ToTable("playback_sessions", t => {
            t.HasCheckConstraint("ck_playback_sessions_status", "status IN ('issued','revoked','expired')");
            t.HasCheckConstraint("ck_playback_sessions_dates", "expires_at > created_at");
            t.HasCheckConstraint("ck_playback_sessions_month", "extract(day from usage_month) = 1 AND usage_month = date_trunc('month', created_at AT TIME ZONE 'UTC')::date");
            t.HasCheckConstraint("ck_playback_sessions_request", "length(btrim(idempotency_key)) > 0 AND request_hash ~ '^[a-f0-9]{64}$'");
        });
        session.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        session.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        session.Property(x => x.Status).HasMaxLength(20).IsRequired();
        session.HasIndex(x => new { x.PartnerId, x.IdempotencyKey }).IsUnique();
        session.HasIndex(x => new { x.PartnerId, x.CreatedAt });
        session.HasIndex(x => x.ExpiresAt);
        session.HasOne<License>().WithMany().HasForeignKey(x => new { x.LicenseId, x.PartnerId }).HasPrincipalKey(x => new { x.Id, x.PartnerId }).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<LicenseVideo>().WithMany().HasForeignKey(x => new { x.LicenseId, x.VideoId }).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<VideoAsset>().WithMany().HasForeignKey(x => new { x.VideoAssetId, x.VideoId }).HasPrincipalKey(x => new { x.Id, x.VideoId }).OnDelete(DeleteBehavior.Restrict);

        var playbackEvent = model.Entity<PlaybackEvent>();
        playbackEvent.ToTable("playback_events", t => {
            t.HasCheckConstraint("ck_playback_events_kind", "kind IN ('started','progress','completed','failed')");
            t.HasCheckConstraint("ck_playback_events_position", "position_ms >= 0");
            t.HasCheckConstraint("ck_playback_events_key", "length(btrim(event_key)) > 0");
        });
        playbackEvent.Property(x => x.EventKey).HasMaxLength(128).IsRequired();
        playbackEvent.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        playbackEvent.HasIndex(x => new { x.PlaybackSessionId, x.EventKey }).IsUnique();
        playbackEvent.HasIndex(x => x.CreatedAt);
        playbackEvent.HasOne<PlaybackSession>().WithMany().HasForeignKey(x => x.PlaybackSessionId).OnDelete(DeleteBehavior.Restrict);

        var usage = model.Entity<UsageMonthly>();
        usage.ToTable("usage_monthly", t => {
            t.HasCheckConstraint("ck_usage_monthly_count", "sessions_issued >= 0");
            t.HasCheckConstraint("ck_usage_monthly_month", "extract(day from month) = 1");
        });
        usage.HasKey(x => new { x.PartnerId, x.LicenseId, x.Month });
        usage.HasOne<License>().WithMany().HasForeignKey(x => new { x.LicenseId, x.PartnerId }).HasPrincipalKey(x => new { x.Id, x.PartnerId }).OnDelete(DeleteBehavior.Restrict);

        var portalUser = model.Entity<PortalUser>();
        portalUser.ToTable("portal_users", t => {
            t.HasCheckConstraint("ck_portal_users_role", "(role = 'admin' AND partner_id IS NULL) OR (role = 'partner' AND partner_id IS NOT NULL)");
            t.HasCheckConstraint("ck_portal_users_username", "username ~ '^[A-Za-z0-9][A-Za-z0-9@._+-]{2,127}$' AND normalized_username = upper(username)");
            t.HasCheckConstraint("ck_portal_users_password", "length(password_hash) >= 32 AND security_stamp <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_portal_users_dates", "updated_at >= created_at");
            t.HasCheckConstraint("ck_portal_users_name", "length(btrim(display_name)) > 0");
            t.HasCheckConstraint("ck_portal_users_failures", "failed_login_count >= 0 AND failed_login_count <= 5");
        });
        portalUser.Property(x => x.Username).HasMaxLength(128).IsRequired();
        portalUser.Property(x => x.NormalizedUsername).HasMaxLength(128).IsRequired();
        portalUser.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        portalUser.Property(x => x.Role).HasMaxLength(20).IsRequired();
        portalUser.Property(x => x.PasswordHash).HasMaxLength(1024).IsRequired();
        portalUser.HasIndex(x => x.NormalizedUsername).IsUnique();
        portalUser.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);

        var import = model.Entity<VideoImportJob>();
        import.Property(job => job.ImportOrigin).HasMaxLength(16);
        import.ToTable("video_import_jobs", t => {
            t.HasCheckConstraint("ck_video_import_jobs_identity", "youtube_id ~ '^[A-Za-z0-9_-]{11}$' AND source_url = 'https://www.youtube.com/watch?v=' || youtube_id");
            t.HasCheckConstraint("ck_video_import_jobs_metadata", "(reference IS NULL OR length(btrim(reference)) BETWEEN 1 AND 128) AND (requested_title IS NULL OR length(btrim(requested_title)) BETWEEN 1 AND 300)");
            t.HasCheckConstraint("ck_video_import_jobs_status", "status IN ('queued','running','completed','failed')");
            t.HasCheckConstraint("ck_video_import_jobs_stage", "stage IN ('queued','probing','downloading','transcoding','uploading','completed','failed')");
            t.HasCheckConstraint("ck_video_import_jobs_progress", "progress_percent BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_video_import_jobs_heights", "cardinality(requested_heights) BETWEEN 1 AND 4 AND requested_heights <@ ARRAY[1080,720,480,360] AND cardinality(completed_heights) <= 4 AND 0 < ALL(completed_heights)");
            t.HasCheckConstraint("ck_video_import_jobs_dates", "updated_at >= created_at");
            t.HasCheckConstraint("ck_video_import_jobs_lease", "(status = 'running' AND lease_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status <> 'running' AND lease_id IS NULL AND lease_expires_at IS NULL)");
        });
        import.Property(x => x.YoutubeId).HasMaxLength(11).IsRequired();
        import.Property(x => x.SourceUrl).HasMaxLength(200).IsRequired();
        import.Property(x => x.Title).HasMaxLength(300);
        import.Property(x => x.Reference).HasMaxLength(128);
        import.Property(x => x.RequestedTitle).HasMaxLength(300);
        import.HasIndex(x => x.Reference).IsUnique().HasFilter("status IN ('queued','running') AND reference IS NOT NULL");
        import.Property(x => x.Status).HasMaxLength(20).IsRequired();
        import.Property(x => x.Stage).HasMaxLength(20).IsRequired();
        import.Property(x => x.ErrorCode).HasMaxLength(64);
        import.Property(x => x.ErrorMessage).HasMaxLength(500);
        import.HasOne<Video>().WithMany().HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.SetNull);
        import.HasIndex(x => x.YoutubeId).IsUnique().HasFilter("status IN ('queued','running')");
        import.HasIndex(x => new { x.Status, x.CreatedAt });
        import.HasIndex(x => x.LeaseExpiresAt).HasFilter("status = 'running'");

        // Keep names deterministic without a provider-specific naming-conventions dependency.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase(property.Name));
            foreach (var key in entity.GetKeys())
                key.SetName($"{(key.IsPrimaryKey() ? "pk" : "ak")}_{entity.GetTableName()}_{string.Join("_", key.Properties.Select(p => SnakeCase(p.Name)))}");
            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName($"ix_{entity.GetTableName()}_{string.Join("_", index.Properties.Select(p => SnakeCase(p.Name)))}");
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName($"fk_{entity.GetTableName()}_{string.Join("_", fk.Properties.Select(p => SnakeCase(p.Name)))}");
        }
    }

    private static string SnakeCase(string name) => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
