// Shared by the synchronous boot script and the module runtime. This stores only
// a language code; it never contains session identifiers or credentials.
(() => {
  if (globalThis.FYXBODYPortalLocalePreference) return;
  const key = 'fyxbody.portal.locale';
  const supported = ['es', 'ca', 'en', 'ar'];
  const normalize = value => {
    if (typeof value !== 'string') return null;
    const locale = value.trim().replaceAll('_', '-').split('-')[0].toLowerCase();
    return supported.includes(locale) ? locale : null;
  };
  function read(options = {}) {
    try {
      const storage = 'storage' in options ? options.storage : globalThis.localStorage;
      const stored = normalize(storage?.getItem(key));
      if (stored) return stored;
    } catch { /* A cookie can retain the preference when storage is blocked. */ }
    try {
      const cookieDocument = 'document' in options ? options.document : globalThis.document;
      for (const part of (cookieDocument?.cookie || '').split(';')) {
        const equals = part.indexOf('=');
        if (equals < 0 || part.slice(0, equals).trim() !== key) continue;
        try {
          const stored = normalize(decodeURIComponent(part.slice(equals + 1).trim()));
          if (stored) return stored;
        } catch { /* Ignore malformed cookie values. */ }
      }
    } catch { /* Language negotiation still works if both stores are blocked. */ }
    return null;
  }
  function write(value, options = {}) {
    const locale = normalize(value);
    if (!locale) return;
    try {
      const storage = 'storage' in options ? options.storage : globalThis.localStorage;
      storage?.setItem(key, locale);
    } catch { /* The cookie is the non-sensitive persistence fallback. */ }
    try {
      const cookieDocument = 'document' in options ? options.document : globalThis.document;
      const protocol = 'protocol' in options ? options.protocol : globalThis.location?.protocol;
      if (cookieDocument) cookieDocument.cookie = `${key}=${locale}; Max-Age=31536000; Path=/portal/; SameSite=Lax${protocol === 'https:' ? '; Secure' : ''}`;
    } catch { /* Current-tab locale changes remain available without persistence. */ }
  }
  globalThis.FYXBODYPortalLocalePreference = Object.freeze({ key, normalize, read, write });
})();
