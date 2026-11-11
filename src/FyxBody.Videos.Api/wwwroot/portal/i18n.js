import './locale-preference.js';
import { messages } from './messages.js';
export { messages, descriptions } from './messages.js';
export const supportedLocales = Object.freeze(['es', 'ca', 'en', 'ar']);
export const storageKey = 'fyxbody.portal.locale';
export function resolveLocale(preference, languages = []) {
  for (const candidate of [preference, ...languages]) {
    const base = globalThis.FYXBODYPortalLocalePreference.normalize(candidate);
    if (base) return base;
  }
  return 'es';
}
const stored = globalThis.FYXBODYPortalLocalePreference.read();
let locale = resolveLocale(stored, globalThis.navigator?.languages || []);
export const getLocale = () => locale;
export const directionFor = value => resolveLocale(value) === 'ar' ? 'rtl' : 'ltr';
const listeners = new Set();
const releasedOwners = new FinalizationRegistry(listener => listeners.delete(listener));
export function onLocaleChange(callback, owner) {
  const listener = { callback, owner: owner ? new WeakRef(owner) : null };
  listeners.add(listener);
  if (owner) releasedOwners.register(owner, listener);
  return () => listeners.delete(listener);
}
export function setLocale(value) {
  const selected = globalThis.FYXBODYPortalLocalePreference.normalize(value);
  if (!selected) return locale;
  locale = selected;
  globalThis.FYXBODYPortalLocalePreference.write(locale);
  if (globalThis.document) { document.documentElement.lang = locale; document.documentElement.dir = directionFor(locale); }
  for (const listener of listeners) {
    const owner = listener.owner?.deref();
    if (listener.owner && !owner?.isConnected) listeners.delete(listener);
    else listener.callback(owner);
  }
  return locale;
}
// This portal uses the ICU argument and cardinal-plural subset. Parsing operates on
// syntax, never translated fragments, HTML or evaluated JavaScript. Complete messages
// may nest plurals; all plural groups require the CLDR `other` fallback.
export function parseMessage(message) {
  let position = 0;
  function read(until) { const start = position; while (position < message.length && !until.includes(message[position])) position++; return message.slice(start, position).trim(); }
  function nodes(nested = false) {
    const result = []; let literal = '';
    const flush = () => { if (literal) { result.push({ type: 'literal', value: literal }); literal = ''; } };
    while (position < message.length) {
      const character = message[position++];
      if (character === '}' && nested) { flush(); return result; }
      if (character !== '{') { literal += character; continue; }
      flush(); const name = read(',}');
      if (!name || position >= message.length) throw new Error('Invalid message argument');
      if (message[position++] === '}') { result.push({ type: 'argument', name }); continue; }
      const type = read(',}');
      if (type !== 'plural' || message[position++] !== ',') throw new Error('Unsupported message syntax');
      const options = {};
      while (position < message.length) {
        while (/\s/.test(message[position] || '') && position < message.length) position++;
        if (message[position] === '}') { position++; break; }
        const key = read('{');
        if (!/^(zero|one|two|few|many|other|=\d+)$/.test(key) || message[position++] !== '{') throw new Error('Invalid plural option');
        options[key] = nodes(true);
      }
      if (!options.other) throw new Error('Missing plural other category');
      result.push({ type: 'plural', name, options });
    }
    if (nested) throw new Error('Unclosed message');
    flush(); return result;
  }
  return nodes();
}
const astCache = new Map();
export function formatFor(targetLocale, key, values = {}) {
  const resolved = resolveLocale(targetLocale);
  const source = messages[resolved]?.[key] ?? messages.es[key];
  if (source === undefined) throw new Error(`Unknown portal message: ${key}`);
  if (!astCache.has(source)) astCache.set(source, parseMessage(source));
  const numberFormat = new Intl.NumberFormat(resolved);
  const rules = new Intl.PluralRules(resolved);
  function render(nodes, pluralCount) {
    return nodes.map(node => {
      if (node.type === 'literal') return pluralCount === undefined ? node.value : node.value.replaceAll('#', numberFormat.format(pluralCount));
      if (!(node.name in values)) throw new Error(`Missing parameter ${node.name} for ${key}`);
      const value = values[node.name];
      if (node.type === 'argument') return '\u2068' + (typeof value === 'number' ? numberFormat.format(value) : String(value)) + '\u2069';
      if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error(`Invalid plural number ${node.name}`);
      return render(node.options[`=${value}`] || node.options[rules.select(value)] || node.options.other, value);
    }).join('');
  }
  return render(astCache.get(source));
}
export const format = (key, values) => formatFor(locale, key, values);
export class LocalizedValue {
  constructor(render) { this.render = render; }
  toString() { return this.render(); }
  [Symbol.toPrimitive]() { return this.toString(); }
}
export const t = (key, values) => new LocalizedValue(() => format(key, values));
export const number = value => new LocalizedValue(() => new Intl.NumberFormat(locale).format(value ?? 0));
export const padded = value => new LocalizedValue(() => new Intl.NumberFormat(locale, { minimumIntegerDigits: 2 }).format(value ?? 0));
export const date = value => new LocalizedValue(() => value ? new Intl.DateTimeFormat(locale, { calendar: 'gregory', dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : '—');
export const percentage = value => new LocalizedValue(() => new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 0 }).format(value));
export const list = values => new LocalizedValue(() => new Intl.ListFormat(locale, { style: 'long', type: 'conjunction' }).format(values.map(String)));
export function bindText(value) {
  const node = document.createTextNode(String(value));
  onLocaleChange(target => { target.data = String(value); }, node);
  return node;
}
export function setText(node, value) { node.replaceChildren(value instanceof LocalizedValue ? bindText(value) : document.createTextNode(String(value))); }
const attributes = new WeakMap();
export function bindAttribute(node, name, value) {
  if (!attributes.has(node)) attributes.set(node, new Map());
  attributes.get(node).set(name, value);
  const update = target => { if (attributes.get(target)?.get(name) === value) target.setAttribute(name, String(value)); };
  update(node); onLocaleChange(update, node);
}
let pageTitle;
export function setTitle(value) { pageTitle = value; document.title = String(value); }
onLocaleChange(() => { if (pageTitle && globalThis.document) document.title = String(pageTitle); });
if (globalThis.document) { document.documentElement.lang = locale; document.documentElement.dir = directionFor(locale); }
