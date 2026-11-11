(() => {
  const preference = globalThis.FYXBODYPortalLocalePreference.read();
  const locale = [preference, ...(navigator.languages || [])].map(globalThis.FYXBODYPortalLocalePreference.normalize).find(Boolean) || 'es';
  document.documentElement.lang = locale;
  document.documentElement.dir = locale === 'ar' ? 'rtl' : 'ltr';
})();
