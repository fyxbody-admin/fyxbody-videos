# FYXBODY Videos — Partner Integration Guide

This guide explains how to integrate your licensed FYXBODY exercise catalogue and video playback into your platform.

## 1. Before you start

Ask your FYXBODY contact for:

- The API base URL for your environment.
- Your partner API credential, in `keyId.secret` format, delivered through a secure channel.
- Confirmation that your partner account, licence and exercise catalogue are active.

Your portal username and password are separate from your API credential. A licence ID is not an authentication credential.

Your platform needs a backend to call FYXBODY. Keep the API credential on that backend; never include it in browser code, a distributed application, a URL or analytics.

All examples below use fictitious values. Replace the base URL, IDs and timestamps with those provided by the API or your FYXBODY contact.

## 2. Integration flow

1. Your backend retrieves the exercises available to your partner account.
2. You map your platform’s exercises to FYXBODY video and asset IDs.
3. When a user wants to play a video, your backend authenticates the user and checks their permission.
4. Your backend requests a playback session from FYXBODY.
5. Your backend returns the temporary playback URL to your player.
6. Your platform sends playback events through your backend and can query monthly usage.

The browser receives a temporary playback URL, never your partner API credential.

## 3. Authentication and response language

Include this header in every request to the FYXBODY API:

```http
Authorization: Bearer <keyId.secret>
```

Use HTTPS for the API URL supplied by FYXBODY. Store the credential in your server’s secret manager or protected environment configuration.

You can request the language of human-readable messages:

```http
Accept-Language: en
```

Supported languages: `en`, `es`, `ca`, `ar`. The response confirms the selected language in `Content-Language`. Handle failures using HTTP status and `code`, not the translated message. JSON field names, IDs, status codes and ISO timestamps remain unchanged. This header does not translate exercise titles or video audio.

## 4. Retrieve your licensed catalogue

### List available videos

```http
GET /v1/videos?page=1&pageSize=25
Authorization: Bearer <keyId.secret>
Accept-Language: en
```

Example response:

```json
{
  "page": 1,
  "pageSize": 25,
  "total": 1,
  "items": [
    {
      "id": "11111111-1111-4111-8111-111111111111",
      "externalExerciseId": "EX-001",
      "title": "Example exercise",
      "assets": [
        {
          "id": "22222222-2222-4222-8222-222222222222",
          "language": "en",
          "rendition": "720p",
          "contentType": "video/mp4",
          "sizeBytes": 7823491,
          "durationMs": 32000
        }
      ]
    }
  ]
}
```

- `page` starts at 1.
- `pageSize` defaults to 25 and supports values from 1 to 100.
- Continue through the pages until you have retrieved the catalogue. Deduplicate by `id` when synchronising, as catalogue changes can shift pages.
- `id` identifies the FYXBODY video. Use `externalExerciseId` as an additional mapping reference where appropriate.
- Each item in `assets` identifies a playable file. Choose an available language and rendition, then use its `id` as `videoAssetId`.
- `durationMs` is the file duration in milliseconds.

The example’s language and rendition are illustrative. Use the values actually returned for your catalogue; do not assume that every exercise has every language or resolution.

### Retrieve one video

```http
GET /v1/videos/{videoId}
Authorization: Bearer <keyId.secret>
```

This returns the video details and available assets.

### How licence access is applied

The API automatically combines the exercises available through your active, current licences. You do not send a `licenseId` when listing videos or requesting playback.

An exercise must still be available and included in your current access. Catalogue availability does not guarantee remaining playback quota. Refresh your catalogue periodically and handle access changes when requesting playback.

## 5. Request a playback session

Call this endpoint from your backend after authorising your user:

```http
POST /v1/videos/{videoId}/playback-sessions
Authorization: Bearer <keyId.secret>
Content-Type: application/json
Idempotency-Key: <unique-playback-attempt-id>
```

```json
{
  "videoAssetId": "22222222-2222-4222-8222-222222222222"
}
```

A successful response contains:

| Field | Meaning |
|---|---|
| `sessionId` | Identifier to associate with subsequent playback events. |
| `videoId` | The authorised video. |
| `videoAssetId` | The authorised file. |
| `expiresAt` | Expiry of the temporary playback URL. |
| `url` | The temporary HTTPS URL to pass to your player. |
| `replayed` | Whether this is a valid repeat of an existing request. |

Use `expiresAt` as the actual expiry. URLs normally last five minutes, may be configured up to fifteen minutes, and cannot outlast the applicable licence.

Return only the necessary playback data to your authenticated client. Set `Cache-Control: no-store` on your own playback response. Do not log or persist the temporary URL in analytics, shared caches or long-term storage.

### Idempotency and retries

Create a unique attempt ID before the first request for a logical playback attempt. A UUID without personal data is suitable. The `Idempotency-Key` header allows 1–128 characters without control characters.

- If a request times out or returns a temporary failure, retry with the **same attempt ID, video and asset**.
- A valid replay returns the same session and expiry with `replayed: true`, without adding another authorised session.
- A new playback attempt or renewal needs a **new attempt ID**.
- Never share an attempt ID across different users, videos or playback attempts.
- Replaying a request does not extend the expiry.

A reused key with a different video or asset returns `409 idempotency_conflict`. An expired session returns `409 session_expired`. A shortened licence can produce `409 session_license_changed`. For the latter two cases, request a new session with a new attempt ID if playback is still authorised. Every replay checks current access again.

## 6. Connect the player in your platform

Your frontend calls an authenticated endpoint in **your own backend**. That endpoint validates the user and exercise, maps the exercise to the FYXBODY video/asset, and requests the FYXBODY session using your server-side credential.

For example, you might implement:

```http
POST /api/exercises/{yourExerciseId}/playback
```

This is an illustrative route in your application, not a FYXBODY endpoint. Return the required `url`, `sessionId` and `expiresAt` to your frontend.

A web player can use the returned URL directly:

```html
<video id="exercise-video" controls playsinline></video>
```

```javascript
const player = document.querySelector('#exercise-video');

// Create one function per logical playback attempt.
// Reuse that function when retrying the same attempt.
function createPlaybackAttempt(exerciseId) {
  const attemptId = crypto.randomUUID();

  return async function openPlayback(csrfToken) {
    const response = await fetch(
      '/api/exercises/' + encodeURIComponent(exerciseId) + '/playback',
      {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-Token': csrfToken
        },
        body: JSON.stringify({ attemptId })
      }
    );

    if (!response.ok) throw new Error(String(response.status));

    const { url, sessionId, expiresAt } = await response.json();
    player.src = url;
    return { sessionId, expiresAt };
  };
}
```

Connect this function to your own UI and authentication flow. Your backend must preserve the attempt ID when forwarding a retry to FYXBODY. Associate the resulting session with the requesting user before accepting events from that user. If your own authentication uses cookies, apply your normal CSRF protection.

An issued playback URL may remain usable until `expiresAt` even if access is subsequently revoked. Do not assume immediate interruption of an already issued playback URL. The API does not provide DRM or single-use playback URLs.

## 7. Report playback events

Send events from your backend:

```http
POST /v1/playback-events
Authorization: Bearer <keyId.secret>
Content-Type: application/json
```

```json
{
  "events": [
    {
      "sessionId": "33333333-3333-4333-8333-333333333333",
      "eventKey": "attempt-started-1",
      "kind": "started",
      "positionMs": 0,
      "occurredAt": "2026-11-05T12:00:00Z"
    }
  ]
}
```

Replace the example session and timestamp with the actual event values.

| Field | Requirement |
|---|---|
| `sessionId` | Session returned by the playback endpoint, belonging to the requesting user. |
| `eventKey` | Unique identifier per event within the session; 1–128 characters without control characters. |
| `kind` | `started`, `progress`, `completed` or `failed`. |
| `positionMs` | Integer between zero and the asset’s `durationMs`. |
| `occurredAt` | Actual event time as an ISO 8601 timestamp; send UTC. |

Send batches of 1–50 events. `occurredAt` must fall between session creation and expiry and be no more than 30 seconds ahead of the FYXBODY server. A batch can arrive up to 24 hours after session expiry; this does not extend the valid event-time window.

Keep the same `eventKey` and payload when retrying an event. An identical duplicate is not counted twice. A different payload under the same key produces `409 event_conflict` and rejects the entire batch. A successful response contains `accepted` and `duplicates`.

Your backend should retain pending batches for bounded retries. Validate that each session belongs to your authenticated user before forwarding their events. Do not include names, email addresses or patient information in event payloads.

## 8. Query monthly usage

```http
GET /v1/usage?month=2026-11
Authorization: Bearer <keyId.secret>
```

Use `YYYY-MM`. If omitted, `month` defaults to the current UTC month.

The response contains:

- `month`.
- `licenses`, with `licenseId`, `licenseName`, `monthlySessionQuota`, `sessionsIssued` and `unlimitedSessions` for each licence.
- `startedSessions` and `completedSessions`.

### Monthly quota and unlimited sessions

With `unlimitedSessions: false`, new authorised sessions count against the licence’s monthly allowance. `monthlySessionQuota: 0` blocks new sessions.

With `unlimitedSessions: true`, there is no monthly session cap and `monthlySessionQuota` is 0. `sessionsIssued` still increases for every new authorised session. Valid replays do not add another session. Unlimited sessions do not remove request-rate limits.

An authorised session counts even if the user never starts the video. `startedSessions` and `completedSessions` count distinct sessions with those reported events in the UTC month of `occurredAt`. They can differ from the month in which sessions were issued. Reported events are not an independent guarantee of actual viewing.

Changing an allowance or licence mode does not reset previous usage. The usage response shows the current licence mode. Access revocation does not erase historical usage.

## 9. Handle errors and request limits

Errors use `application/problem+json`:

```json
{
  "type": "about:blank",
  "title": "Example error message",
  "status": 401,
  "code": "invalid_credentials",
  "requestId": "example-request-reference"
}
```

Keep the HTTP status, `code` and `requestId` for troubleshooting. `X-Request-Id` also identifies the request. Do not include your API credential or temporary playback URLs in support logs.

| HTTP | Recommended handling |
|---|---|
| 400 | Correct JSON, IDs, pagination, month, event data or the idempotency key. |
| 401 | Check the API credential, its expiry and account status. Do not retry indefinitely. |
| 403 / 404 | Check licence validity, catalogue access and the selected video/asset. |
| 409 | Inspect `code` for idempotency conflicts, expired sessions, licence changes or conflicting events. |
| 429 | Honour `Retry-After` and inspect `code` before retrying. |
| 503 | Use bounded retries with the same session/event keys and unchanged payload. |

For `429 monthly_quota_exceeded`, query usage. The indicated wait runs to the next UTC month; rapid retries do not restore quota. Other `429` responses can indicate request-rate limits.

Use timeouts, bounded retries, backoff and jitter. Do not automatically retry every `4xx` response. FYXBODY can confirm the request allowance for your integration; always honour the response’s `Retry-After` value.

## 10. Integration resources and final checks

The partner portal’s **Documentation** section provides:

- This integration flow in English, Spanish, Catalan and Arabic.
- Copyable request examples.
- A downloadable partner OpenAPI contract.
- An optional Node.js client example for use exclusively in your backend. Using Node.js is not a requirement for your integration.

Before enabling playback for your users, confirm that:

- You are using the correct environment, credential, catalogue and video/asset mapping.
- The credential never reaches the frontend or logs.
- Retrying a timed-out playback request preserves its attempt ID.
- New playback attempts and renewals use new attempt IDs.
- You handle expiry, denied access and exhausted quota.
- Event retries retain the same key and content, and sessions are checked against the requesting user.
- Your usage reporting distinguishes authorised sessions from reported starts and completions.


## Find a video by exercise reference

For FYXBODY legacy imports, `externalExerciseId` is the CSV `Referencia` in the exact form `<Order>#<Id>`, using the original video record values. For example, call `GET /v1/videos?externalExerciseId=1%235fad4883b60edc0c1a9e5793` with your partner bearer token. The `%23` encodes the literal `#`.
URL-encode the exercise reference when building the query string. The match is
exact and case-sensitive. The response keeps the catalogue format, with one item
if the video is available under your licence, or `total: 0` and `items: []` otherwise.
Use the returned item’s `id` to create a playback session.
