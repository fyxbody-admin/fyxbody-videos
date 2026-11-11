import test from 'node:test';
import assert from 'node:assert/strict';
import '../../src/FyxBody.Videos.Api/wwwroot/portal/locale-preference.js';
const { key, read, write } = globalThis.FYXBODYPortalLocalePreference;
const blockedStorage = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } };

test('valid local preference wins over cookie without being overwritten by reads', () => {
  const storage = { getItem: () => 'ca-ES', setItem: () => assert.fail('read must not write') };
  const document = { cookie: `${key}=ar` };
  assert.equal(read({ storage, document }), 'ca');
  assert.equal(document.cookie, `${key}=ar`);
});
test('missing, blocked or invalid local storage reads the valid cookie on reload', () => {
  const document = { cookie: `unrelated=1; ${key}=ar-EG` };
  for (const storage of [null, blockedStorage, { getItem: () => 'unsupported' }]) assert.equal(read({ storage, document }), 'ar');
  assert.equal(read({ storage: null, document: { cookie: `${key}=%invalid` } }), null);
});
test('explicit selection survives through a host-only preference cookie when storage is blocked', () => {
  const document = { cookie: '' };
  write('ar', { storage: blockedStorage, document, protocol: 'https:' });
  assert.match(document.cookie, /^fyxbody\.portal\.locale=ar;/);
  assert.match(document.cookie, /Max-Age=31536000; Path=\/portal\/; SameSite=Lax; Secure$/);
  assert.doesNotMatch(document.cookie, /Domain=|HttpOnly/i);
  assert.equal(read({ storage: blockedStorage, document }), 'ar');
  const unchanged = document.cookie;
  write('unsupported', { storage: blockedStorage, document, protocol: 'https:' });
  assert.equal(document.cookie, unchanged);
});
test('local HTTP and fully blocked stores retain graceful negotiation without throwing', () => {
  const document = { cookie: '' };
  write('en', { storage: null, document, protocol: 'http:' });
  assert.doesNotMatch(document.cookie, /Secure/);
  const deniedDocument = { get cookie() { throw new Error('denied'); }, set cookie(value) { throw new Error('denied'); } };
  assert.equal(read({ storage: blockedStorage, document: deniedDocument }), null);
  assert.doesNotThrow(() => write('ca', { storage: blockedStorage, document: deniedDocument }));
});
test('unsupported runtime selection cannot overwrite a valid active preference', async () => {
  const { setLocale, getLocale } = await import('../../src/FyxBody.Videos.Api/wwwroot/portal/i18n.js');
  setLocale('ar');
  setLocale('unsupported');
  assert.equal(getLocale(), 'ar');
  setLocale('es');
});
