# FYXBODY 2.0 video integration

Engineering handoff for Sergio and Claude. Implement the FYXBODY 2.0 integration using the existing partner API. The delegated catalogue and private upload API is deployed in Azure TEST with migration 007.

## Azure TEST quick start

Base URL: `https://app-fyxbody2-videos-api-test-zpfyqf.azurewebsites.net`. FYXBODY 2.0 is configured as principal partner `fyxbody-2-0`. Its source licence ID in TEST is `71069da8-ac77-4e0c-a7b5-42bde6760384`. Successfully imported and published videos are added to its source catalogue automatically. Delegated subcatalogues use snapshots and must be reconciled separately.

Set these variables on the backend. Obtain the TEST token through a private channel.

```bash
export VIDEOS_API='https://app-fyxbody2-videos-api-test-zpfyqf.azurewebsites.net'
export VIDEOS_PARTNER_TOKEN='<TEST partner token>'
export SOURCE_LICENSE_ID='71069da8-ac77-4e0c-a7b5-42bde6760384'

# List the first catalogue page. Read total and pageSize to paginate.
curl --fail-with-body --get "$VIDEOS_API/v1/videos" \
  -H "Authorization: Bearer $VIDEOS_PARTNER_TOKEN" \
  --data-urlencode 'page=1' --data-urlencode 'pageSize=100'

# Create a professional. Save the returned id as SUBPARTNER_ID.
curl --fail-with-body -X POST "$VIDEOS_API/v1/subpartners" \
  -H "Authorization: Bearer $VIDEOS_PARTNER_TOKEN" \
  -H 'Content-Type: application/json' \
  --data '{"code":"professional-001","name":"Professional 001","status":"active"}'

# Create a licence with a 20 GB private upload quota. Set dates for the intended term.
# Save the returned id as CHILD_LICENSE_ID.
curl --fail-with-body -X POST "$VIDEOS_API/v1/subpartners/$SUBPARTNER_ID/licenses" \
  -H "Authorization: Bearer $VIDEOS_PARTNER_TOKEN" \
  -H 'Content-Type: application/json' \
  --data '{"name":"Professional 001 TEST","status":"active","startsAt":"2026-11-10T00:00:00Z","endsAt":"2027-02-01T00:00:00Z","monthlySessionQuota":0,"unlimitedSessions":true,"allowPartnerUploads":true,"uploadQuotaMode":"storage","uploadQuotaLimit":20000000000}'

# Assign the current source catalogue. For a subset, send videoIds instead of allCurrent.
curl --fail-with-body -X PUT "$VIDEOS_API/v1/subpartners/$SUBPARTNER_ID/licenses/$CHILD_LICENSE_ID/catalogue" \
  -H "Authorization: Bearer $VIDEOS_PARTNER_TOKEN" \
  -H 'Content-Type: application/json' \
  --data "{\"sourceLicenseId\":\"$SOURCE_LICENSE_ID\",\"allCurrent\":true}"

# Issue a separate backend credential scoped to this professional.
curl --fail-with-body -X POST "$VIDEOS_API/v1/subpartners/$SUBPARTNER_ID/credentials" \
  -H "Authorization: Bearer $VIDEOS_PARTNER_TOKEN" \
  -H 'Content-Type: application/json' \
  --data '{"expiresAt":"2027-02-01T00:00:00Z"}'
```

The credential response returns `apiKey` once. Store it securely and use it for calls that act as the professional, including private uploads and playback. Save the IDs returned by the creation calls before running the subsequent commands.

## 1. Integrate the imported catalogue

Configure FYXBODY 2.0 as a standard principal partner with an active licence, delegated partner and catalogue management enabled, and unlimited private uploads. Apply the same configuration to any partner with equivalent entitlements. Grant that licence access to every successfully imported, published video in the FYXBODY catalogue. Reconcile the grants after each import batch so the catalogue grows as videos become available. Keep a report of CSV rows that were excluded, failed, or are still pending. The import is ongoing, so catalogue availability must be checked at runtime.

Keep the API base URL and partner bearer credential on the FYXBODY 2.0 backend. Pass the player only the temporary playback URL and the session information it needs. Use the partner API contract for catalogue, playback, events and usage.

For imported videos, CSV `Referencia` becomes the API field `externalExerciseId`. Its exact format is `<Order>#<Id>`, where `Order` and `Id` come from the legacy video record. Preserve the original values and the separator. If FYXBODY 2.0 assigns new identifiers or changes ordering, retain the original reference on migration.

Example reference from the export: `1#5fad4883b60edc0c1a9e5793`.

```bash
curl --get "$VIDEOS_API/v1/videos" \
  --header "Authorization: Bearer $VIDEOS_PARTNER_TOKEN" \
  --data-urlencode "externalExerciseId=1#5fad4883b60edc0c1a9e5793"
```

The query value on the wire is `1%235fad4883b60edc0c1a9e5793`. Use a query-string builder or URL encoding so the server receives the full reference. The lookup is exact and returns a catalogue response with one item if the partner has access, or `items: []` otherwise.

Use the item's `id` and the chosen asset's `id` in `POST /v1/videos/{videoId}/playback-sessions` with body `{"videoAssetId":"…"}` and an `Idempotency-Key`. Reuse that key when retrying the same playback attempt. Open the returned temporary URL in the player and report events through the existing partner API. If the exercise has no result yet, render its video as unavailable and resolve the reference again on a later visit.

## 2. Configure principal partners and delegated catalogues

Use the same Partner entity for principal partners and professionals. A nullable `parentPartnerId` identifies a direct subpartner. Limit the hierarchy to two levels. The platform administrator configures which principal licences allow subpartner management, catalogue delegation and quota assignment. Apply identical authorization rules to every principal partner. A principal may create and manage only its direct subpartners within its configured entitlements.

The platform assigns a source catalogue to the principal. The principal can create a catalogue for each subpartner by selecting all current videos or a subset of the videos it can delegate. A principal can also select ready videos it owns. Store grants by video ID, without copying files. A subpartner may use its delegated catalogue and its own private uploads in the same application.

A delegated grant requires an active child licence and a valid, delegable source entitlement on the principal side. Recheck both at list, lookup and playback time, including idempotent playback retries. If the source grant is removed, the principal licence expires, or a video is removed from the subcatalogue, the child cannot obtain a new playback URL. Previously issued playback URLs expire on their normal short lifetime. Subpartners cannot delegate onward. Reject grants across principal groups, including attempts made through the admin catalogue editor.

| Principal partner | Licence or catalogue | Private upload quota |
|---|---|---|
| FYXBODY 2.0 | Own uploads | Unlimited |
| FYXBODY 2.0 | Professional 1 | 20 GB |
| FYXBODY 2.0 | Professional 2 | Unlimited |
| FYXBODY 2.0 | Professional 3 | 100 videos |
| Partner 2 | Subpartner 2.1 | 10 GB |
| Partner 2 | Subpartner 2.2 | 50 videos |

## 3. Add licence-controlled private uploads

Use the partner OpenAPI contract in `docs/openapi/partner.json`.

- Manage direct children, their licences and their bearer credentials under `/v1/subpartners`. Keep credentials on the backend.
- Read or replace grants from a source licence at `/v1/subpartners/{subpartnerId}/licenses/{licenseId}/catalogue`. Send `sourceLicenseId` plus `videoIds` or `allCurrent: true`. The latter captures the videos available at the time of the request. Repeat reconciliation when new imports become available.
- Upload with `POST /v1/licenses/{licenseId}/private-videos`. Send multipart fields `file`, `title`, `language`, `rendition`, `durationMs` and optional `partnerReference`. Keep the same `Idempotency-Key` when retrying the same payload.
- Read quota and usage at `GET /v1/licenses/{licenseId}/private-video-usage`. Update title and reference with PATCH or remove the video with DELETE at `/v1/private-videos/{videoId}`.
- Use `uploadQuotaMode` values `storage`, `videos` or `unlimited`. Send the limit as bytes or a video count in `uploadQuotaLimit`. Omit the limit for unlimited licences.

1. Add `allowPartnerUploads` to licence configuration. Default it to false for existing and new licences. An administrator can enable it per licence. The partner selects the target licence or catalogue when uploading. Resolve the partner from the authenticated identity and verify that the target licence is active, belongs to that partner and permits uploads.
2. Store the owning partner on each private video. Existing imported videos keep their shared catalogue behavior. Assign each private video a stable API ID and a server-generated unique `externalExerciseId` in a reserved namespace. Keep the caller's optional free-form value in a separate field, proposed as `partnerReference`. Return it to the owner and administrators. The partner can edit or clear it and use it as metadata in its own system. Validation allows up to 128 characters and no control characters, with duplicates allowed. Use the API video ID to identify or mutate a specific video.
3. Add authenticated partner routes to upload an MP4 and edit its metadata. Reuse the existing upload validation and private storage rules. The initial scope is an MP4 upload with a title and optional `partnerReference`. Associate the video with the selected licence and make it playable only when its asset is ready. Make upload retries idempotent so retrying a timed-out request cannot create a second video.
4. A private upload starts visible only to the owning partner and platform administrators. A principal may explicitly add its own private video to a direct subpartner's catalogue. That grants viewing and playback while both licences and the grant remain valid. Editing, deleting and editing `partnerReference` stay with the owner and administrators. Other principals, their subpartners and siblings without a grant cannot see the video. A professional's own uploads stay private to that professional and platform administrators. Managing a professional's licence, catalogue and usage does not grant the principal access to their private files. Apply these rules to listing, reference lookup, details, playback, mutations and catalogue edits.
5. Treat `allowPartnerUploads` as permission to create uploads. Turning it off blocks new uploads. Existing private videos remain playable by the owning partner while their licence and grant are valid. Test licence expiry or revocation between upload and publication, and between playback attempts.
6. After the API ships, add the FYXBODY 2.0 UI for the owning partner. Show upload controls only for eligible licences, allow the partner to enter or edit its free-form reference, and display the processing status. Keep upload authorization on the Video API even when the UI hides the control.

## 4. Configure private upload quotas per licence

Keep `allowPartnerUploads` as the permission to upload. When an administrator enables it, require them to choose exactly one quota mode for that licence.

| Mode | Limit | What counts |
|---|---|---|
| Storage | A positive number of GB | Total bytes occupied by the private video files charged to the licence, including each retained quality. Use 1 GB = 1,000,000,000 bytes. |
| Video count | A positive whole number of videos | One slot per private video, regardless of how many qualities it has. |
| Unlimited | No quota value | Track both storage and video count for reporting. Normal file validation and per-file limits still apply. |

Charge each upload to the licence selected at creation and track that allocation independently of catalogue visibility. Imported catalogue videos and delegated videos do not consume the recipient’s private upload quota. A principal-owned upload shared with multiple professionals remains charged once to its originating licence. An unlimited principal licence does not make its subpartner licences unlimited. The usage endpoint reports current stored bytes, video count and configured limit. Quota checks are serialized on the licence during upload.

Check and reserve capacity atomically when accepting an upload. Count a pending upload against the quota, and make a repeated idempotent request reuse its existing reservation. Under a storage quota, reconcile against actual retained bytes and check capacity before persisting any additional quality. Release unused reservations after failure or cancellation. Restore storage capacity after the underlying files are deleted, and restore the video slot after the private video is removed. If an administrator lowers the limit below current usage, retain existing videos and block new uploads until usage falls below the limit. Return a stable quota-exceeded error that the FYXBODY 2.0 UI can explain.

## 5. Acceptance checks

- Resolve a migrated exercise by its exact legacy `Order#Id`, with the hash encoded in the URL, and play the returned video.
- Reconcile the dedicated FYXBODY 2.0 licence against the published import. Produce a list of unresolved CSV references and make a second reconciliation safe to run.
- Verify that partner A can upload with an enabled licence and receives a denial with a disabled, expired or foreign licence. Replaying the same upload request must return the original video.
- Verify that A can see and play its ready private video and recover the optional `partnerReference`. The administrator can view and manage it.
- Verify that a principal can share its own video with one direct child. A sibling without the grant and any partner outside that group cannot list, find or play it, even with its exact reference or ID. Verify that removing the child grant or losing the upstream entitlement blocks new playback URLs.
- Verify that switching uploads off blocks new uploads while the original private video remains accessible under the owner's valid grant.
- In storage mode, verify that multiple qualities contribute their actual byte sizes. In video-count mode, verify that the same video consumes one slot. In unlimited mode, verify that usage is still reported.
- Verify that simultaneous uploads cannot exceed the quota, retries do not reserve capacity twice, and failed or deleted uploads release capacity when cleanup is complete.
- Verify that a second principal can be configured with the same delegation features as FYXBODY 2.0 and can manage only its own children. Verify that delegated videos occupy no capacity on the child licence and that its private quota is enforced independently.
