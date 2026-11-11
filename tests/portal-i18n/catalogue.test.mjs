import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { messages, descriptions, parseMessage, formatFor, resolveLocale, directionFor, setLocale, getLocale, t, number, date, percentage, list } from '../../src/FyxBody.Videos.Api/wwwroot/portal/i18n.js';
const portal = new URL('../../src/FyxBody.Videos.Api/wwwroot/portal/', import.meta.url);
const keys = Object.keys(messages.es).sort();
function inspect(nodes, parameters = new Set(), plurals = []) {
  for (const node of nodes) {
    if (node.name) parameters.add(node.name);
    if (node.type === 'plural') { plurals.push(node); for (const children of Object.values(node.options)) inspect(children, parameters, plurals); }
  }
  return { parameters: [...parameters].sort(), plurals };
}
const stripIsolation = value => value.replace(/[\u2068\u2069]/g, '');
test('four complete catalogues, semantic keys and translator context', () => {
  assert.ok(keys.length >= 340);
  for (const [locale, catalogue] of Object.entries(messages)) {
    assert.deepEqual(Object.keys(catalogue).sort(), keys, locale);
    for (const key of keys) { assert.ok(catalogue[key].trim(), `${locale}:${key}`); assert.match(key, /^[a-z][a-zA-Z]+\.[a-zA-Z0-9.]+$/); assert.ok(descriptions[key]?.length > 10, key); }
  }
});
test('parameter parity, valid plural fallbacks and all Arabic CLDR categories', () => {
  for (const key of keys) {
    const source = inspect(parseMessage(messages.es[key]));
    for (const locale of Object.keys(messages)) {
      const parsed = inspect(parseMessage(messages[locale][key]));
      assert.deepEqual(parsed.parameters, source.parameters, `${locale}:${key}`);
      for (const plural of parsed.plurals) {
        assert.ok(plural.options.other, `${locale}:${key}`);
        if (locale === 'ar') for (const category of ['zero', 'one', 'two', 'few', 'many', 'other']) assert.ok(plural.options[category], `${key}:${category}`);
      }
      for (const count of [0, 1, 2, 3, 11, 100, 1234]) {
        const params = Object.fromEntries(parsed.parameters.map(name => [name, name === 'count' ? count : `test-${name}`]));
        const rendered = formatFor(locale, key, params);
        assert.ok(!rendered.includes('{count,'), `${locale}:${key}`);
      }
    }
  }
});
test('regional negotiation, explicit preference and stable Spanish fallback', () => {
  assert.equal(resolveLocale('ca-ES', ['en-US']), 'ca');
  assert.equal(resolveLocale('ar-EG'), 'ar');
  assert.equal(resolveLocale(null, ['de-DE', 'en-GB']), 'en');
  assert.equal(resolveLocale('unsupported', ['ca-ES']), 'ca');
  assert.equal(resolveLocale(null, ['pt-BR']), 'es');
  for (const locale of ['es', 'ca', 'en', 'ar']) assert.equal(directionFor(locale), locale === 'ar' ? 'rtl' : 'ltr');
  assert.equal(formatFor('pt-BR', 'action.cancel'), messages.es['action.cancel']);
});
test('missing messages and missing or invalid named parameters fail explicitly', () => {
  assert.throws(() => formatFor('en', 'missing.message'), /Unknown portal message/);
  assert.throws(() => formatFor('en', 'error.reference'), /Missing parameter/);
  assert.throws(() => formatFor('ar', 'pager.summary', { count: '3', page: 1, pages: 1 }), /Invalid plural number/);
  assert.throws(() => parseMessage('{count, plural, one {one}}'), /Missing plural other/);
});
test('reactive values format dates, numbers, percentages and lists for current locale', () => {
  setLocale('en');
  const label = t('usage.authorised'), count = number(1234), when = date('2026-11-05T12:00:00Z');
  assert.equal(String(label), 'Authorised sessions');
  assert.equal(String(count), new Intl.NumberFormat('en').format(1234));
  setLocale('ar');
  assert.equal(getLocale(), 'ar');
  assert.equal(String(label), messages.ar['usage.authorised']);
  assert.equal(String(count), new Intl.NumberFormat('ar').format(1234));
  assert.match(String(count), /\p{Nd}/u);
  assert.equal(String(when), new Intl.DateTimeFormat('ar', { calendar: 'gregory', dateStyle: 'medium', timeStyle: 'short' }).format(new Date('2026-11-05T12:00:00Z')));
  assert.equal(String(percentage(.35)), new Intl.NumberFormat('ar', { style: 'percent', maximumFractionDigits: 0 }).format(.35));
  assert.equal(String(list(['es', 'en'])), new Intl.ListFormat('ar', { type: 'conjunction', style: 'long' }).format(['es', 'en']));
  setLocale('es');
});
test('technical identifiers and authored data retain exact Unicode and order', () => {
  const sourceId = 'ejercicios/A-123_تمرين/es-720p.mp4';
  const title = 'Sentadilla — قرفصاء 🏋🏽';
  for (const locale of Object.keys(messages)) {
    assert.ok(stripIsolation(formatFor(locale, 'video.sourceId', { sourceId })).includes(sourceId));
    assert.ok(stripIsolation(formatFor(locale, 'preview.videoLabel', { title })).includes(title));
    assert.match(formatFor(locale, 'video.sourceId', { sourceId }), /\u2068.*\u2069/u);
  }
});
test('source routes use explicit messages, locale headers and isolated Unicode digit tiles', async () => {
  const app = await readFile(new URL('app.js', portal), 'utf8');
  for (const [, key] of app.matchAll(/\bt\('([^']+)'/g)) assert.ok(messages.es[key], key);
  assert.doesNotMatch(app, /MutationObserver|toLocaleLowerCase\('es'\)|\[0-9\]\/\.test\(character\)/);
  assert.match(app, /\\p\{Nd\}\/u\.test\(character\)/);
  assert.match(app, /class: 'digits', role: 'img', dir: 'ltr'/);
  assert.match(app, /xhr\.setRequestHeader\('Accept-Language', getLocale\(\)\)/);
  assert.equal((app.match(/'Accept-Language': getLocale\(\)/g) || []).length, 2);
  assert.match(app, /dir: 'ltr'.*temporaryPassword/);
  assert.doesNotMatch(app, /\.innerHTML|insertAdjacentHTML/);
});
test('pseudo-expanded messages keep argument and plural syntax intact', () => {
  function pseudo(nodes) { return nodes.map(node => node.type === 'literal' ? { ...node, value: `[${node.value} ~~~~~]` } : node.type === 'plural' ? { ...node, options: Object.fromEntries(Object.entries(node.options).map(([key, children]) => [key, pseudo(children)])) } : node); }
  for (const key of keys) {
    const ast = parseMessage(messages.en[key]);
    assert.deepEqual(inspect(pseudo(ast)).parameters, inspect(ast).parameters);
  }
});

test('explicit text/attribute bindings switch locale without replacing input or selection state', async () => {
  const { bindText, bindAttribute, setText, setTitle } = await import('../../src/FyxBody.Videos.Api/wwwroot/portal/i18n.js');
  const previousDocument = globalThis.document;
  class Node {
    constructor(data = '') { this.data = data; this.isConnected = true; this.children = []; this.attributes = {}; }
    replaceChildren(...children) { for (const child of this.children) child.isConnected = false; this.children = children; }
    setAttribute(name, value) { this.attributes[name] = value; }
  }
  globalThis.document = { createTextNode: text => new Node(text), documentElement: { lang: 'es', dir: 'ltr' }, title: '' };
  try {
    setLocale('es');
    const heading = bindText(t('nav.catalogue'));
    const button = new Node();
    setText(button, t('password.show'));
    bindAttribute(button, 'aria-label', t('password.showLabel', { field: t('field.password') }));
    const input = { value: 'Ángel password 123', checked: true };
    const selected = new Set(['exercise-1', 'exercise-2']);
    setTitle(t('page.title', { page: t('nav.catalogue') }));
    setLocale('ar');
    assert.equal(heading.data, messages.ar['nav.catalogue']);
    assert.equal(button.children[0].data, messages.ar['password.show']);
    assert.ok(button.attributes['aria-label'].includes(messages.ar['field.password']));
    assert.equal(document.documentElement.dir, 'rtl');
    assert.equal(input.value, 'Ángel password 123');
    assert.equal(input.checked, true);
    assert.equal(selected.size, 2);
    assert.ok(document.title.includes(messages.ar['nav.catalogue']));
    setText(button, t('password.hide'));
    setLocale('ca');
    assert.equal(button.children[0].data, messages.ca['password.hide']);
    assert.equal(document.documentElement.dir, 'ltr');
    assert.equal(heading.data, messages.ca['nav.catalogue']);
  } finally { globalThis.document = previousDocument; setLocale('es'); }
});
