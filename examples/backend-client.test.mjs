import test from 'node:test';
import assert from 'node:assert/strict';
import { VideosClient, VideosApiError } from './backend-client.mjs';

const baseUrl = 'https://videos.example.test';
const apiKey = 'local-example-key.local-example-secret';

async function withFetch(stub, run) {
  const original = globalThis.fetch;
  globalThis.fetch = stub;
  try { await run(); } finally { globalThis.fetch = original; }
}

test('rejects external origins and non-partner paths before exposing Authorization', async () => {
  const client = new VideosClient({ baseUrl, apiKey });
  let calls = 0;
  await withFetch(async () => { calls++; throw new Error('must not send'); }, async () => {
    for (const path of ['https://attacker.example/v1/videos', '//attacker.example/v1/videos', '/admin/v1/partners', '/v1/../admin/partners', 'https://user:password@videos.example.test/v1/videos']) {
      await assert.rejects(() => client.request(path), /Request must target/);
    }
  });
  assert.equal(calls, 0);
  assert.equal(JSON.stringify(client).includes(apiKey), false);
});

test('POST sends the supplied idempotency key and does not retry an ambiguous failure', async () => {
  const client = new VideosClient({ baseUrl, apiKey });
  const attemptId = 'same-logical-playback-attempt';
  const requests = [];
  await withFetch(async (url, options) => {
    requests.push({ url: url.toString(), ...options });
    return new Response(JSON.stringify({ code: 'storage_unavailable', requestId: 'request-example' }), {
      status: 503, headers: { 'Content-Type': 'application/problem+json' }
    });
  }, async () => {
    await assert.rejects(() => client.playback('video-id', 'asset-id', attemptId), error => {
      assert.ok(error instanceof VideosApiError);
      assert.equal(error.status, 503);
      assert.equal(error.requestId, 'request-example');
      return true;
    });
    assert.equal(requests.length, 1);
    assert.equal(requests[0].headers.Authorization, `Bearer ${apiKey}`);
    assert.equal(requests[0].headers['Idempotency-Key'], attemptId);
    assert.deepEqual(JSON.parse(requests[0].body), { videoAssetId: 'asset-id' });
    assert.equal(requests[0].redirect, 'error');
    assert.equal(requests[0].cache, 'no-store');
    // A retry is a caller decision; preserve the key for that same attempt.
    await assert.rejects(() => client.playback('video-id', 'asset-id', attemptId), VideosApiError);
    assert.equal(requests.length, 2);
    assert.equal(requests[1].headers['Idempotency-Key'], attemptId);
  });
});

test('surfaces Retry-After without automatically retrying quota errors', async () => {
  const client = new VideosClient({ baseUrl, apiKey });
  let calls = 0;
  await withFetch(async () => {
    calls++;
    return new Response(JSON.stringify({ code: 'monthly_quota_exceeded' }), {
      status: 429, headers: { 'Retry-After': '86400', 'Content-Type': 'application/problem+json' }
    });
  }, async () => {
    await assert.rejects(() => client.playback('video-id', 'asset-id', 'attempt'), error => {
      assert.equal(error.code, 'monthly_quota_exceeded');
      assert.equal(error.retryAfter, '86400');
      return true;
    });
  });
  assert.equal(calls, 1);
});
