import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { runInNewContext } from 'node:vm';
import { readFile } from 'node:fs/promises';
import { buildDocumentationExamples, documentationSections, focusDocumentationSection } from '../../src/FyxBody.Videos.Api/wwwroot/portal/documentation.js';
import { messages, descriptions, formatFor, setLocale } from '../../src/FyxBody.Videos.Api/wwwroot/portal/i18n.js';

const baseUrl = 'https://videos.example.invalid';
const portal = new URL('../../src/FyxBody.Videos.Api/wwwroot/portal/', import.meta.url);
test('four-language documentation renders all messages without unbound ICU path placeholders', () => {
  const keys = Object.keys(messages.es).filter(key => key.startsWith('docs.') || key === 'nav.documentation');
  assert.ok(keys.length > 50);
  for (const locale of ['es', 'ca', 'en', 'ar']) {
    for (const key of keys) {
      assert.ok(descriptions[key]?.length > 10, key);
      assert.equal(formatFor(locale, key), messages[locale][key], `${locale}:${key}`);
    }
    for (const section of documentationSections) assert.ok(messages[locale][`docs.${section}.title`], `${locale}:${section}`);
  }
});

test('copyable code remains exact ASCII and valid across locale switches', () => {
  for (const locale of ['es', 'ca', 'en', 'ar']) {
    setLocale(locale);
    const examples = buildDocumentationExamples({ origin: baseUrl, locale });
    for (const [name, value] of Object.entries(examples)) {
      assert.doesNotMatch(value, /[^\x09\x0A\x0D\x20-\x7E]/u, `${locale}:${name}`);
      assert.doesNotMatch(value, /licenseId|keyId\.secret|https:\/\/[^\s]*[?&]sig=/u, name);
    }
    assert.ok(examples.setup.includes(`VIDEOS_LANGUAGE='${locale}'`));
    const catalogue = JSON.parse(examples.catalogueResponse);
    assert.equal(catalogue.items[0].assets[0].id, '22222222-2222-4222-8222-222222222222');
    assert.equal(catalogue.items[0].assets[0].durationMs, 32000);
  }
  setLocale('es');
  assert.throws(() => buildDocumentationExamples({ origin: 'javascript:alert(1)', locale: 'en' }), /Invalid API origin/);
});

test('shell examples produce valid JSON and preserve attempt identity on retry without any network', () => {
  const examples = buildDocumentationExamples({ origin: baseUrl, locale: 'ar' });
  // curl is replaced by a local recorder, so this test cannot issue a request.
  const recorder = `curl() { for arg in "$@"; do printf '%s\\0' "$arg"; done; printf '\\0'; }`;
  const script = [recorder, examples.setup, examples.catalogue, examples.session, examples.session, examples.events, examples.usage].join('\n');
  const result = spawnSync('/bin/sh', ['-eu', '-c', script], { encoding: 'utf8', env: { ...process.env, VIDEOS_PARTNER_KEY: 'test-only-not-a-credential' } });
  assert.equal(result.status, 0, result.stderr);
  const requests = result.stdout.split('\0\0').filter(Boolean).map(request => request.split('\0'));
  assert.equal(requests.length, 6);
  for (const request of requests) {
    assert.ok(request.includes('Authorization: Bearer test-only-not-a-credential'));
    assert.ok(request.includes('Accept-Language: ar'));
    assert.ok(request.some(arg => arg.startsWith(baseUrl + '/v1/')));
  }
  const sessionRequests = requests.filter(args => args.some(arg => arg.endsWith('/playback-sessions')));
  assert.equal(sessionRequests.length, 2);
  const keys = sessionRequests.map(args => args.find(arg => arg.startsWith('Idempotency-Key: ')));
  assert.equal(keys[0], keys[1]);
  assert.match(keys[0], /^Idempotency-Key: [a-f0-9-]{36}$/);
  for (const args of sessionRequests) assert.deepEqual(JSON.parse(args[args.indexOf('--data') + 1]), { videoAssetId: '22222222-2222-4222-8222-222222222222' });
  const eventArgs = requests.find(args => args.some(arg => arg.endsWith('/playback-events')));
  const body = JSON.parse(eventArgs[eventArgs.indexOf('--data') + 1]);
  assert.equal(body.events.length, 1);
  assert.deepEqual(Object.keys(body.events[0]), ['sessionId', 'eventKey', 'kind', 'positionMs', 'occurredAt']);
});

test('internal section links focus only known documentation targets', () => {
  const calls = [];
  const main = { querySelector: selector => { calls.push(selector); return { focus: options => calls.push(options), scrollIntoView: options => calls.push(options) }; } };
  focusDocumentationSection(main, 'catalogue');
  assert.deepEqual(calls, ['#documentation-catalogue', { preventScroll: true }, { block: 'start' }]);
  calls.length = 0;
  focusDocumentationSection(main, 'unknown');
  focusDocumentationSection(main, 'catalogue, input');
  assert.deepEqual(calls, []);
});

test('documentation is partner-only, inert and uses explicit LTR code and locale bindings', async () => {
  const [app, docs, css] = await Promise.all(['app.js', 'documentation.js', 'styles.css'].map(file => readFile(new URL(file, portal), 'utf8')));
  const adminNav = app.match(/const adminNav = (.*);/)[1];
  const partnerNav = app.match(/const partnerNav = (.*);/)[1];
  assert.ok(!adminNav.includes('documentation'));
  assert.ok(partnerNav.includes('documentation'));
  assert.match(docs, /el\('pre', \{ dir: 'ltr'/);
  assert.match(docs, /onLocaleChange\(target => \{ target.textContent = examples\(\)\[name\]/);
  assert.match(docs, /navigator.clipboard.writeText\(code.textContent\)/);
  assert.doesNotMatch(docs, /innerHTML|insertAdjacentHTML|eval\(|new Function|crossorigin|fetch\(url|request\(/);
  assert.match(css, /\.documentation-example pre[^}]*overflow-x: auto/);
  assert.match(docs, /download: 'fyxbody-videos-openapi.json'/);
  assert.match(docs, /download: 'fyxbody-videos-client.mjs'/);
});


test('player example stays inert and scopes stable attempt IDs to a single logical playback', async () => {
  const player = {}, calls = []; let sequence = 0;
  const context = {
    document: { querySelector: () => player },
    crypto: { randomUUID: () => `attempt-${++sequence}` },
    fetch: async (url, options) => { calls.push({ url, options }); return { ok: true, json: async () => ({ url: 'https://media.example.invalid/temporary', sessionId: 'test-session', expiresAt: '2026-11-05T12:05:00Z' }) }; }
  };
  const source = buildDocumentationExamples({ origin: baseUrl, locale: 'ar' }).player.match(/<script type="module">([\s\S]*?)<\/script>/)[1];
  runInNewContext(source, context);
  assert.equal(calls.length, 0);
  const first = context.createPlaybackAttempt('exercise/a');
  await first('csrf-test'); await first('csrf-test');
  await context.createPlaybackAttempt('exercise/b')('csrf-test');
  assert.equal(calls[0].url, '/api/exercises/exercise%2Fa/playback');
  assert.equal(calls[0].options.headers['X-CSRF-Token'], 'csrf-test');
  assert.equal(calls[0].options.headers.Authorization, undefined);
  assert.equal(JSON.parse(calls[0].options.body).attemptId, JSON.parse(calls[1].options.body).attemptId);
  assert.notEqual(JSON.parse(calls[0].options.body).attemptId, JSON.parse(calls[2].options.body).attemptId);
  assert.equal(player.src, 'https://media.example.invalid/temporary');
});
