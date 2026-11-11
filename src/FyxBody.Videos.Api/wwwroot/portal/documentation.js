import { t, getLocale, onLocaleChange, setText, formatFor, resolveLocale } from './i18n.js';

export const documentationSections = Object.freeze(['access', 'connection', 'catalogue', 'session', 'player', 'events', 'usage', 'errors', 'checklist']);
// Shared structure keeps the visible guide and Markdown export in sync.
const guideContent = [
  ['access', ['identity', 'license', 'security'], []],
  ['connection', ['environment', 'language'], ['setup']],
  ['catalogue', ['pagination', 'lookup', 'selection', 'notUrl'], ['catalogue', 'catalogueResponse']],
  ['session', ['request', 'retry', 'conflicts'], ['session']],
  ['player', ['flow', 'attempt', 'expiry'], ['player']],
  ['events', ['batch', 'validation', 'dedup'], ['events']],
  ['usage', ['contract', 'quota', 'events'], ['usage']],
  ['errors', ['problem'], []],
  ['checklist', [], []]
];
const errorRows = [['400', 'badRequest'], ['401', 'unauthorized'], ['403 / 404', 'unavailable'], ['409', 'conflict'], ['429', 'rateLimit'], ['503', 'temporary']];
const exampleLanguages = { setup: 'sh', catalogue: 'sh', catalogueResponse: 'json', session: 'sh', player: 'html', events: 'sh', usage: 'sh' };

const shellQuote = value => "'" + String(value).replaceAll("'", "'\\''") + "'";
// Plain code strings intentionally bypass ICU: arguments must never gain bidi controls.
export function buildDocumentationExamples({ origin, locale }) {
  const api = new URL(origin);
  if (!['https:', 'http:'].includes(api.protocol)) throw new TypeError('Invalid API origin');
  const language = ['es', 'ca', 'en', 'ar'].includes(locale) ? locale : 'es';
  const auth = '  -H "Authorization: Bearer $VIDEOS_PARTNER_KEY" \\\n  -H "Accept-Language: $VIDEOS_LANGUAGE"';
  return {
    setup: `export VIDEOS_API=${shellQuote(api.origin)}
export VIDEOS_LANGUAGE=${shellQuote(language)}
: "\${VIDEOS_PARTNER_KEY:?}"
export PLAYBACK_ATTEMPT_ID="$(node -p 'require("node:crypto").randomUUID()')"`,
    catalogue: `curl --fail-with-body "$VIDEOS_API/v1/videos?page=1&pageSize=25" \\\n${auth}

export VIDEO_ID='11111111-1111-4111-8111-111111111111'
curl --fail-with-body "$VIDEOS_API/v1/videos/$VIDEO_ID" \\\n${auth}`,
    catalogueResponse: JSON.stringify({ page: 1, pageSize: 25, total: 1, items: [{ id: '11111111-1111-4111-8111-111111111111', externalExerciseId: 'EX-001', title: 'Example exercise', assets: [{ id: '22222222-2222-4222-8222-222222222222', language: 'es', rendition: 'original', contentType: 'video/mp4', sizeBytes: 7823491, durationMs: 32000 }] }] }, null, 2),
    session: `export VIDEO_ASSET_ID='22222222-2222-4222-8222-222222222222'
curl --fail-with-body "$VIDEOS_API/v1/videos/$VIDEO_ID/playback-sessions" \\\n  -X POST \\\n${auth} \\\n  -H 'Content-Type: application/json' \\\n  -H "Idempotency-Key: $PLAYBACK_ATTEMPT_ID" \\\n  --data "{\\\"videoAssetId\\\":\\\"$VIDEO_ASSET_ID\\\"}"`,
    player: `<video id="exercise-video" controls playsinline></video>
<script type="module">
const player = document.querySelector('#exercise-video');
function createPlaybackAttempt(exerciseId) {
  const attemptId = crypto.randomUUID();
  return async function openPlayback(csrfToken) {
    const response = await fetch(
      '/api/exercises/' + encodeURIComponent(exerciseId) + '/playback', {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-Token': csrfToken
        },
        body: JSON.stringify({ attemptId })
      });
    if (!response.ok) throw new Error(String(response.status));
    const { url, sessionId, expiresAt } = await response.json();
    player.src = url;
    return { sessionId, expiresAt };
  };
}
</script>`,
    events: `curl --fail-with-body "$VIDEOS_API/v1/playback-events" \\\n  -X POST \\\n${auth} \\\n  -H 'Content-Type: application/json' \\\n  --data '{"events":[{"sessionId":"33333333-3333-4333-8333-333333333333","eventKey":"attempt-started-1","kind":"started","positionMs":0,"occurredAt":"2026-11-05T12:00:00Z"}]}'`,
    usage: `curl --fail-with-body "$VIDEOS_API/v1/usage?month=2026-11" \\\n${auth}`
  };
}

export function buildDocumentationMarkdown({ origin, locale }) {
  const language = resolveLocale(locale);
  const text = key => formatFor(language, key);
  const examples = buildDocumentationExamples({ origin, locale: language });
  const apiOrigin = new URL(origin).origin;
  const lines = ['# FYXBODY Videos — ' + text('nav.documentation'), '', text('docs.description'), '', text('docs.referenceOnly'), ''];
  const cell = value => value.replaceAll('|', '\\|').replaceAll('\n', ' ');
  for (const [name, paragraphs, exampleNames] of guideContent) {
    lines.push('## ' + text(`docs.${name}.title`), '');
    if (name === 'connection') lines.push(text('docs.connection.base') + ': `' + apiOrigin + '`', '');
    for (const key of paragraphs) lines.push(text(`docs.${name}.${key}`), '');
    for (const example of exampleNames) lines.push('### ' + text(`docs.example.${example}`), '', '```' + exampleLanguages[example], examples[example], '```', '');
    if (name === 'errors') {
      lines.push('| ' + cell(text('docs.errors.status')) + ' | ' + cell(text('docs.errors.action')) + ' |', '| --- | --- |');
      for (const [status, key] of errorRows) lines.push('| ' + status + ' | ' + cell(text(`docs.errors.${key}`)) + ' |');
      lines.push('', text('docs.errors.limits'), '');
    }
    if (name === 'checklist') {
      for (const key of ['access', 'retry', 'events']) lines.push('- ' + text(`docs.checklist.${key}`));
      lines.push('', '### ' + text('docs.downloads.title'), '', text('docs.downloads.backendOnly'), '',
        '[' + text('docs.downloads.openapi') + '](' + apiOrigin + '/portal/integration/partner-openapi.json)', '',
        '[' + text('docs.downloads.client') + '](' + apiOrigin + '/portal/integration/backend-client.mjs.txt)', '');
    }
  }
  return lines.join('\n');
}

export function focusDocumentationSection(main, section) {
  if (!documentationSections.includes(section)) return;
  const target = main.querySelector(`#documentation-${section}`);
  if (!target) return;
  target.focus({ preventScroll: true });
  target.scrollIntoView({ block: 'start' });
}

export function renderDocumentationPage(main, { el, button, head }) {
  const downloadStatus = el('p', { role: 'status', class: 'documentation-copy-status' });
  const downloadMarkdown = button(t('docs.downloads.markdown'), () => {
    let url;
    let link;
    try {
      const locale = getLocale();
      const markdown = buildDocumentationMarkdown({ origin: location.origin, locale });
      url = URL.createObjectURL(new Blob([markdown], { type: 'text/markdown;charset=utf-8' }));
      link = el('a', { href: url, download: `fyxbody-integration-guide-${locale}.md` });
      document.body.append(link); link.click();
      setText(downloadStatus, t('docs.downloads.ready'));
    } catch {
      setText(downloadStatus, t('docs.downloads.failed'));
    } finally {
      link?.remove();
      if (url) setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
  }, 'primary');
  head(main, t('nav.documentation'), t('docs.description'), downloadMarkdown);
  const wrapper = el('div', { class: 'documentation' });
  const intro = el('div', { class: 'info space-bottom' }, t('docs.referenceOnly'));
  const contents = el('nav', { class: 'documentation-index', 'aria-label': t('docs.index') },
    el('h2', {}, t('docs.index')),
    el('ol', {}, documentationSections.map(section => el('li', {}, el('a', { href: `#documentation/${section}` }, t(`docs.${section}.title`))))));
  wrapper.append(downloadStatus, intro, contents);
  const examples = () => buildDocumentationExamples({ origin: location.origin, locale: getLocale() });
  function codeExample(name) {
    const captionId = `documentation-example-${name}`;
    const code = el('code', { dir: 'ltr' }, examples()[name]);
    onLocaleChange(target => { target.textContent = examples()[name]; }, code);
    const pre = el('pre', { dir: 'ltr', tabindex: '0', 'aria-labelledby': captionId }, code);
    const status = el('p', { role: 'status', class: 'documentation-copy-status' });
    const copy = button(t('docs.copy'), async () => {
      try {
        await navigator.clipboard.writeText(code.textContent);
        setText(status, t('docs.copied'));
      } catch {
        pre.focus();
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(code);
        selection?.removeAllRanges(); selection?.addRange(range);
        setText(status, t('docs.copyError'));
      }
    }, 'small quiet', { 'aria-describedby': captionId });
    return el('figure', { class: 'documentation-example' },
      el('div', { class: 'documentation-example-head' }, el('figcaption', { id: captionId }, t(`docs.example.${name}`)), copy), pre, status);
  }
  function section(name, paragraphs, exampleNames = []) {
    const headingId = `documentation-${name}`;
    const body = el('section', { class: 'panel documentation-section', 'aria-labelledby': headingId },
      el('div', { class: 'panel-head' }, el('h2', { id: headingId, tabindex: '-1' }, t(`docs.${name}.title`))),
      paragraphs.map(key => el('p', {}, t(`docs.${name}.${key}`))), exampleNames.map(codeExample));
    wrapper.append(body); return body;
  }
  const sections = new Map(guideContent.map(([name, paragraphs, exampleNames]) => [name, section(name, paragraphs, exampleNames)]));
  const connection = sections.get('connection');
  const base = el('div', { class: 'documentation-origin' }, el('span', {}, t('docs.connection.base')), el('code', { dir: 'ltr' }, location.origin));
  connection.querySelector('.panel-head').after(base);
  const errors = sections.get('errors');
  errors.append(el('div', { class: 'table-wrap documentation-errors', tabindex: '0', role: 'region', 'aria-label': t('docs.errors.title') },
    el('table', {}, el('thead', {}, el('tr', {}, el('th', { scope: 'col' }, t('docs.errors.status')), el('th', { scope: 'col' }, t('docs.errors.action')))),
      el('tbody', {}, errorRows.map(([status, key]) => el('tr', {}, el('th', { scope: 'row', dir: 'ltr' }, status), el('td', {}, t(`docs.errors.${key}`))))))), el('p', {}, t('docs.errors.limits')));
  const checklist = sections.get('checklist');
  checklist.append(el('ul', {}, ['access', 'retry', 'events'].map(key => el('li', {}, t(`docs.checklist.${key}`)))));
  checklist.append(el('h3', {}, t('docs.downloads.title')), el('p', {}, t('docs.downloads.backendOnly')),
    el('div', { class: 'actions documentation-downloads' },
      el('a', { class: 'button navigation', href: '/portal/integration/partner-openapi.json', download: 'fyxbody-videos-openapi.json' }, t('docs.downloads.openapi')),
      el('a', { class: 'button navigation', href: '/portal/integration/backend-client.mjs.txt', download: 'fyxbody-videos-client.mjs' }, t('docs.downloads.client'))));
  main.append(wrapper);
}
