DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'videos') THEN
        CREATE SCHEMA videos;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS videos."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'videos') THEN
            CREATE SCHEMA videos;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.partners (
        id uuid NOT NULL,
        code character varying(64) NOT NULL,
        name character varying(200) NOT NULL,
        status character varying(20) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_partners_id PRIMARY KEY (id),
        CONSTRAINT ck_partners_code CHECK (code ~ '^[a-z0-9][a-z0-9_-]*$' AND length(btrim(name)) > 0),
        CONSTRAINT ck_partners_status CHECK (status IN ('active','suspended','revoked'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.videos (
        id uuid NOT NULL,
        external_exercise_id character varying(128) NOT NULL,
        title character varying(300) NOT NULL,
        status character varying(20) NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_videos_id PRIMARY KEY (id),
        CONSTRAINT ck_videos_dates CHECK (updated_at >= created_at),
        CONSTRAINT ck_videos_identity CHECK (length(btrim(external_exercise_id)) > 0 AND length(btrim(title)) > 0),
        CONSTRAINT ck_videos_status CHECK (status IN ('draft','active','archived'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.licenses (
        id uuid NOT NULL,
        partner_id uuid NOT NULL,
        name character varying(200) NOT NULL,
        status character varying(20) NOT NULL,
        starts_at timestamp with time zone NOT NULL,
        ends_at timestamp with time zone NOT NULL,
        monthly_session_quota bigint NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_licenses_id PRIMARY KEY (id),
        CONSTRAINT ak_licenses_id_partner_id UNIQUE (id, partner_id),
        CONSTRAINT ck_licenses_dates CHECK (ends_at > starts_at),
        CONSTRAINT ck_licenses_name CHECK (length(btrim(name)) > 0),
        CONSTRAINT ck_licenses_quota CHECK (monthly_session_quota >= 0),
        CONSTRAINT ck_licenses_status CHECK (status IN ('active','suspended','revoked')),
        CONSTRAINT fk_licenses_partner_id FOREIGN KEY (partner_id) REFERENCES videos.partners (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.partner_credentials (
        id uuid NOT NULL,
        partner_id uuid NOT NULL,
        key_id character varying(128) NOT NULL,
        secret_hash character varying(512) NOT NULL,
        expires_at timestamp with time zone,
        revoked_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_partner_credentials_id PRIMARY KEY (id),
        CONSTRAINT ck_partner_credentials_dates CHECK ((expires_at IS NULL OR expires_at > created_at) AND (revoked_at IS NULL OR revoked_at >= created_at)),
        CONSTRAINT ck_partner_credentials_hash CHECK (length(btrim(secret_hash)) >= 32 AND length(btrim(key_id)) > 0),
        CONSTRAINT fk_partner_credentials_partner_id FOREIGN KEY (partner_id) REFERENCES videos.partners (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.video_assets (
        id uuid NOT NULL,
        video_id uuid NOT NULL,
        language character varying(35) NOT NULL,
        rendition character varying(50) NOT NULL,
        blob_container character varying(63) NOT NULL,
        blob_name character varying(1024) NOT NULL,
        content_type character varying(100) NOT NULL,
        size_bytes bigint NOT NULL,
        duration_ms integer NOT NULL,
        status character varying(20) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_video_assets_id PRIMARY KEY (id),
        CONSTRAINT ak_video_assets_id_video_id UNIQUE (id, video_id),
        CONSTRAINT ck_video_assets_blob CHECK (length(btrim(blob_container)) > 0 AND length(btrim(blob_name)) > 0),
        CONSTRAINT ck_video_assets_language CHECK (language ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$' AND length(btrim(rendition)) > 0),
        CONSTRAINT ck_video_assets_sizes CHECK (size_bytes >= 0 AND duration_ms >= 0),
        CONSTRAINT ck_video_assets_status CHECK (status IN ('pending','ready','disabled')),
        CONSTRAINT fk_video_assets_video_id FOREIGN KEY (video_id) REFERENCES videos.videos (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.license_videos (
        license_id uuid NOT NULL,
        video_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_license_videos_license_id_video_id PRIMARY KEY (license_id, video_id),
        CONSTRAINT fk_license_videos_license_id FOREIGN KEY (license_id) REFERENCES videos.licenses (id) ON DELETE RESTRICT,
        CONSTRAINT fk_license_videos_video_id FOREIGN KEY (video_id) REFERENCES videos.videos (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.usage_monthly (
        partner_id uuid NOT NULL,
        license_id uuid NOT NULL,
        month date NOT NULL,
        sessions_issued bigint NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_usage_monthly_partner_id_license_id_month PRIMARY KEY (partner_id, license_id, month),
        CONSTRAINT ck_usage_monthly_count CHECK (sessions_issued >= 0),
        CONSTRAINT ck_usage_monthly_month CHECK (extract(day from month) = 1),
        CONSTRAINT fk_usage_monthly_license_id_partner_id FOREIGN KEY (license_id, partner_id) REFERENCES videos.licenses (id, partner_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.playback_sessions (
        id uuid NOT NULL,
        partner_id uuid NOT NULL,
        license_id uuid NOT NULL,
        video_id uuid NOT NULL,
        video_asset_id uuid NOT NULL,
        idempotency_key character varying(128) NOT NULL,
        request_hash character varying(64) NOT NULL,
        status character varying(20) NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        usage_month date NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_playback_sessions_id PRIMARY KEY (id),
        CONSTRAINT ck_playback_sessions_dates CHECK (expires_at > created_at),
        CONSTRAINT ck_playback_sessions_month CHECK (extract(day from usage_month) = 1 AND usage_month = date_trunc('month', created_at AT TIME ZONE 'UTC')::date),
        CONSTRAINT ck_playback_sessions_request CHECK (length(btrim(idempotency_key)) > 0 AND request_hash ~ '^[a-f0-9]{64}$'),
        CONSTRAINT ck_playback_sessions_status CHECK (status IN ('issued','revoked','expired')),
        CONSTRAINT fk_playback_sessions_license_id_partner_id FOREIGN KEY (license_id, partner_id) REFERENCES videos.licenses (id, partner_id) ON DELETE RESTRICT,
        CONSTRAINT fk_playback_sessions_license_id_video_id FOREIGN KEY (license_id, video_id) REFERENCES videos.license_videos (license_id, video_id) ON DELETE RESTRICT,
        CONSTRAINT fk_playback_sessions_video_asset_id_video_id FOREIGN KEY (video_asset_id, video_id) REFERENCES videos.video_assets (id, video_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE TABLE videos.playback_events (
        id uuid NOT NULL,
        playback_session_id uuid NOT NULL,
        event_key character varying(128) NOT NULL,
        kind character varying(20) NOT NULL,
        position_ms integer NOT NULL,
        occurred_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_playback_events_id PRIMARY KEY (id),
        CONSTRAINT ck_playback_events_key CHECK (length(btrim(event_key)) > 0),
        CONSTRAINT ck_playback_events_kind CHECK (kind IN ('started','progress','completed','failed')),
        CONSTRAINT ck_playback_events_position CHECK (position_ms >= 0),
        CONSTRAINT fk_playback_events_playback_session_id FOREIGN KEY (playback_session_id) REFERENCES videos.playback_sessions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_license_videos_video_id ON videos.license_videos (video_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_licenses_partner_id_status_starts_at ON videos.licenses (partner_id, status, starts_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE UNIQUE INDEX ix_partner_credentials_key_id ON videos.partner_credentials (key_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_partner_credentials_partner_id ON videos.partner_credentials (partner_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE UNIQUE INDEX ix_partners_code ON videos.partners (code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_playback_events_created_at ON videos.playback_events (created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE UNIQUE INDEX ix_playback_events_playback_session_id_event_key ON videos.playback_events (playback_session_id, event_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_playback_sessions_expires_at ON videos.playback_sessions (expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_playback_sessions_license_id_partner_id ON videos.playback_sessions (license_id, partner_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_playback_sessions_license_id_video_id ON videos.playback_sessions (license_id, video_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_playback_sessions_partner_id_created_at ON videos.playback_sessions (partner_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE UNIQUE INDEX ix_playback_sessions_partner_id_idempotency_key ON videos.playback_sessions (partner_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_playback_sessions_video_asset_id_video_id ON videos.playback_sessions (video_asset_id, video_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE INDEX ix_usage_monthly_license_id_partner_id ON videos.usage_monthly (license_id, partner_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE UNIQUE INDEX ix_video_assets_blob_container_blob_name ON videos.video_assets (blob_container, blob_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE UNIQUE INDEX ix_video_assets_video_id_language_rendition ON videos.video_assets (video_id, language, rendition);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    CREATE UNIQUE INDEX ix_videos_external_exercise_id ON videos.videos (external_exercise_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105143156_InitialVideoLicensing') THEN
    INSERT INTO videos."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261105143156_InitialVideoLicensing', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105145124_RevokeCatalogGrants') THEN
    ALTER TABLE videos.license_videos ADD revoked_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105145124_RevokeCatalogGrants') THEN
    ALTER TABLE videos.license_videos ADD CONSTRAINT ck_license_videos_revoked CHECK (revoked_at IS NULL OR revoked_at >= created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105145124_RevokeCatalogGrants') THEN
    INSERT INTO videos."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261105145124_RevokeCatalogGrants', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105152349_PortalUsers') THEN
    CREATE TABLE videos.portal_users (
        id uuid NOT NULL,
        username character varying(128) NOT NULL,
        normalized_username character varying(128) NOT NULL,
        display_name character varying(200) NOT NULL,
        role character varying(20) NOT NULL,
        partner_id uuid,
        is_active boolean NOT NULL,
        must_change_password boolean NOT NULL,
        password_hash character varying(1024) NOT NULL,
        security_stamp uuid NOT NULL,
        failed_login_count integer NOT NULL,
        lockout_until timestamp with time zone,
        updated_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_portal_users_id PRIMARY KEY (id),
        CONSTRAINT ck_portal_users_dates CHECK (updated_at >= created_at),
        CONSTRAINT ck_portal_users_failures CHECK (failed_login_count >= 0 AND failed_login_count <= 5),
        CONSTRAINT ck_portal_users_name CHECK (length(btrim(display_name)) > 0),
        CONSTRAINT ck_portal_users_password CHECK (length(password_hash) >= 32 AND security_stamp <> '00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT ck_portal_users_role CHECK ((role = 'admin' AND partner_id IS NULL) OR (role = 'partner' AND partner_id IS NOT NULL)),
        CONSTRAINT ck_portal_users_username CHECK (username ~ '^[A-Za-z0-9][A-Za-z0-9@._+-]{2,127}$' AND normalized_username = upper(username)),
        CONSTRAINT fk_portal_users_partner_id FOREIGN KEY (partner_id) REFERENCES videos.partners (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105152349_PortalUsers') THEN
    CREATE UNIQUE INDEX ix_portal_users_normalized_username ON videos.portal_users (normalized_username);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105152349_PortalUsers') THEN
    CREATE INDEX ix_portal_users_partner_id ON videos.portal_users (partner_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM videos."__EFMigrationsHistory" WHERE "MigrationId" = '20261105152349_PortalUsers') THEN
    INSERT INTO videos."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261105152349_PortalUsers', '10.0.14');
    END IF;
END $EF$;
COMMIT;

