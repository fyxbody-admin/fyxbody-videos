import test from 'node:test';
import assert from 'node:assert/strict';
import { buildDocumentationMarkdown, buildDocumentationExamples, documentationSections } from '../../src/FyxBody.Videos.Api/wwwroot/portal/documentation.js';
import { formatFor, setLocale } from '../../src/FyxBody.Videos.Api/wwwroot/portal/i18n.js';
const origin = 'https://videos.example.invalid';

test('Markdown contains the complete integration guide and exact code in all four languages', () => {
  for (const locale of ['es', 'ca', 'en', 'ar']) {
    const markdown = buildDocumentationMarkdown({ origin, locale });
    assert.ok(markdown.startsWith('# FYXBODY Videos — ' + formatFor(locale, 'nav.documentation')));
    for (const section of documentationSections) assert.ok(markdown.includes('## ' + formatFor(locale, `docs.${section}.title`)), `${locale}:${section}`);
    for (const text of Object.values(buildDocumentationExamples({ origin, locale }))) assert.ok(markdown.includes('\n' + text + '\n```'), locale);
    assert.ok(markdown.includes(formatFor(locale, 'docs.errors.rateLimit')));
    assert.ok(markdown.includes(formatFor(locale, 'docs.checklist.events')));
    assert.ok(markdown.includes(origin + '/portal/integration/partner-openapi.json'));
    assert.ok(markdown.includes(origin + '/portal/integration/backend-client.mjs.txt'));
    const jsonBlock = /```json\n([\s\S]*?)\n```/.exec(markdown);
    assert.equal(JSON.parse(jsonBlock[1]).items[0].assets[0].durationMs, 32000);
    assert.equal((markdown.match(/^```/gm) || []).length, 14);
    assert.doesNotMatch(markdown, /[\u2066-\u2069]|Azure|PostgreSQL|App Service|\.NET/);
    assert.equal(Buffer.from(markdown, 'utf8').toString('utf8'), markdown);
  }
});

test('download content uses the requested language even after a different portal selection', () => {
  setLocale('ar');
  const english = buildDocumentationMarkdown({ origin, locale: 'en' });
  setLocale('es');
  assert.equal(buildDocumentationMarkdown({ origin, locale: 'en' }), english);
  assert.ok(english.includes("VIDEOS_LANGUAGE='en'"));
  assert.ok(!english.includes(formatFor('ar', 'docs.description')));
});

test('export strips URL credentials/query and normalizes regional or unsupported languages', () => {
  const markdown = buildDocumentationMarkdown({ origin: 'https://user:do-not-export@videos.example.invalid/path?token=do-not-export', locale: 'ar-SA' });
  assert.ok(markdown.includes("VIDEOS_LANGUAGE='ar'"));
  assert.doesNotMatch(markdown, /do-not-export|user:|token=/);
  assert.ok(buildDocumentationMarkdown({ origin, locale: 'unsupported' }).includes(formatFor('es', 'docs.description')));
  assert.throws(() => buildDocumentationMarkdown({ origin: 'javascript:alert(1)', locale: 'en' }), /Invalid API origin/);
});
