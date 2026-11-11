import { managementViews } from './management.js';
import { openCsvImport, downloadCsvTemplate } from './csv-import.js';
import { renderDocumentationPage, focusDocumentationSection } from './documentation.js';
import { usageMonthIds, formatUsageMonth, usagePeriodValue, parseUsagePeriod } from './usage-period.js';
import { observeHeaderHeight } from './layout.js';
import { t, LocalizedValue, getLocale, setLocale, onLocaleChange, bindText, bindAttribute, setText, setTitle, number, padded, date, percentage, list, format } from './i18n.js';
const $ = (s, root = document) => root.querySelector(s);
const app = $('#app');
const state = { user: null, csrf: '', route: '', revision: 0, partnerId: '', month: new Date().toISOString().slice(0, 7) };
let routeCleanup = () => {};
let shellCleanup = () => {};
function cleanupRoute() { routeCleanup(); routeCleanup = () => {}; }
const labels = { active: t('status.active'), suspended: t('status.suspended'), revoked: t('status.revoked'), draft: t('status.draft'), archived: t('status.archived'), pending: t('status.pending'), ready: t('status.ready'), disabled: t('status.disabled'), admin: t('role.admin'), partner: t('role.partner') };
const id = () => `f-${crypto.randomUUID()}`;
function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === null || value === undefined || value === false) continue;
    if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'class') node.className = value;
    else if (key === 'text') setText(node, value);
    else if (key === 'checked' || key === 'disabled' || key === 'required' || key === 'readOnly') node[key] = Boolean(value);
    else if (value instanceof LocalizedValue) bindAttribute(node, key, value);
    else node.setAttribute(key, value === true ? '' : String(value));
  }
  for (const child of children.flat(Infinity)) if (child !== null && child !== undefined && child !== false) node.append(child instanceof Node ? child : child instanceof LocalizedValue ? bindText(child) : document.createTextNode(String(child)));
  return node;
}
function button(text, action, kind = '', attrs = {}) { return el('button', { type: 'button', class: `button ${kind}`, onclick: action, ...attrs }, text); }
function badge(value) {
  const tone = value === 'ready' ? 'complete' : ['pending', 'draft'].includes(value) ? 'pending' : value === 'suspended' ? 'warning' : ['revoked', 'disabled'].includes(value) ? 'danger' : value === 'archived' ? 'neutral' : '';
  return el('span', { class: `badge ${tone}` }, labels[value] || value);
}
function pixelIcon(type) {
  const shapes = {
    overview: [[2,2,5,5],[9,2,5,5],[2,9,5,5],[9,9,5,5]],
    videos: [[1,3,14,2],[1,5,2,7],[13,5,2,7],[1,12,14,2],[6,6,2,5],[8,7,2,3],[10,8,1,1]],
    partners: [[3,2,4,4],[9,2,4,4],[2,7,6,2],[8,7,6,2],[1,9,6,5],[9,9,6,5]],
    licenses: [[3,1,10,2],[3,3,2,12],[11,3,2,12],[5,13,6,2],[6,5,4,1],[6,8,4,1],[6,11,3,1]],
    users: [[5,1,6,2],[4,3,2,4],[10,3,2,4],[6,6,4,2],[3,9,10,2],[2,11,2,4],[12,11,2,4],[4,13,8,2]],
    usage: [[1,13,14,2],[2,9,3,4],[7,6,3,7],[12,2,3,11],[2,6,2,1],[5,4,3,1],[9,1,3,1]],
    documentation: [[1,2,6,2],[9,2,6,2],[1,4,2,10],[13,4,2,10],[7,3,2,12],[3,12,4,2],[9,12,4,2],[3,6,3,1],[10,6,3,1],[3,9,3,1],[10,9,3,1]],
    settings: [[6,1,4,2],[3,3,3,2],[10,3,3,2],[1,6,3,4],[12,6,3,4],[3,11,3,2],[10,11,3,2],[6,13,4,2],[5,5,6,1],[5,10,6,1],[5,6,1,4],[10,6,1,4]],
    menu: [[2,3,12,2],[2,7,12,2],[2,11,12,2]]
  };
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  for (const [key, value] of Object.entries({ viewBox: '0 0 16 16', width: '16', height: '16', class: 'pixel-icon', 'aria-hidden': 'true', focusable: 'false', 'shape-rendering': 'crispEdges' })) svg.setAttribute(key, value);
  for (const [x,y,width,height] of shapes[type === 'catalogue' ? 'videos' : type] || shapes.overview) {
    const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    for (const [key,value] of Object.entries({ x,y,width,height })) rect.setAttribute(key, String(value));
    svg.append(rect);
  }
  return el('span', { class: 'icon-screen', 'aria-hidden': 'true' }, svg);
}
function digits(value) {
  const display = el('div', { class: 'digits', role: 'img', dir: 'ltr', 'aria-label': number(value) });
  const draw = target => target.replaceChildren(...[...String(padded(value))].map(character => el('span', { class: /\p{Nd}/u.test(character) ? 'digit' : 'digit-separator', 'aria-hidden': 'true' }, character)));
  draw(display); onLocaleChange(draw, display); return display;
}
function statsPanel(name, cells) { return panel(name, el('div', { class: 'stats' }, cells)); }
function technical(value) { return el('bdi', { dir: 'ltr' }, value); }
function title(text, secondary) { return el('div', {}, el('span', { class: 'title', dir: 'auto' }, text), secondary ? el('small', { dir: 'auto' }, secondary) : null); }
function brand() { return el('div', { class: 'brand', dir: 'ltr', 'aria-label': t('brand.accessible') }, 'FYXBODY', el('span', {}, 'VIDEOS')); }
function loading(message = t('loading.data')) { return el('div', { class: 'empty', role: 'status' }, el('span', { class: 'spinner', 'aria-hidden': 'true' }), el('p', {}, message)); }
function empty(message, action) { return el('div', { class: 'empty' }, el('p', {}, message), action); }
function notify(message) { const node = el('div', { class: 'notice' }, message); $('#notices').replaceChildren(node); setTimeout(() => node.remove(), 6000); }
class LocalizedError extends Error { constructor(message) { super(message); this.localizedMessage = message; } }
function errorBox(error) {
  return el('div', { class: 'error', role: 'alert' }, el('p', {}, error.localizedMessage || (error instanceof TypeError ? t('error.network') : error instanceof SyntaxError ? t('error.response') : error.message) || t('error.operation')), error.requestId ? el('small', {}, t('error.reference', { requestId: error.requestId })) : null);
}
class ApiError extends Error { constructor(message, status, requestId) { super(message); this.localizedMessage = message instanceof LocalizedValue ? message : null; this.status = status; this.requestId = requestId; } }
async function request(path, options = {}) {
  const headers = { Accept: 'application/json', ...options.headers, 'Accept-Language': getLocale() };
  const multipart = options.body instanceof FormData;
  if (options.body !== undefined && !multipart) headers['Content-Type'] = 'application/json';
  if (options.method && options.method !== 'GET') headers['X-CSRF-TOKEN'] = state.csrf;
  const response = await fetch(path, { ...options, headers, credentials: 'same-origin', body: options.body === undefined || multipart ? options.body : JSON.stringify(options.body) });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    const error = new ApiError(problem.detail || problem.title || t('error.http', { status: number(response.status) }), response.status, problem.requestId || problem.extensions?.requestId || response.headers.get('X-Request-ID'));
    if ((response.status === 401 || response.status === 403) && !path.startsWith('/portal/api/')) {
      try { await session(); } catch { state.user = null; }
      if (!state.user) renderLogin(t('auth.expired'));
      else if (state.user.mustChangePassword) renderShell('settings');
    }
    throw error;
  }
  return response.status === 204 ? null : response.json();
}
async function session() { const result = await request('/portal/api/session'); state.user = result.user; state.csrf = result.csrfToken; return result; }
async function all(path) {
  const items = []; let page = 1; let result;
  do { result = await request(`${path}${path.includes('?') ? '&' : '?'}page=${page++}&pageSize=100`); items.push(...result.items); } while (items.length < result.total && result.items.length);
  return items;
}
function field(text, name, options = {}) {
  const inputId = id();
  const input = options.choices ? el('select', { id: inputId, name, required: options.required !== false }, options.choices.map(option => {
    const [value, label] = Array.isArray(option) ? option : [option, labels[option] || option];
    return el('option', { value, dir: 'auto' }, label);
  })) : el('input', { id: inputId, name, type: options.type || 'text', required: options.required !== false, maxlength: options.maxlength, min: options.min, max: options.max, step: options.step, autocomplete: options.autocomplete || 'off', placeholder: options.placeholder, pattern: options.pattern });
  input.dir = ['password', 'url', 'number', 'datetime-local', 'month', 'file'].includes(input.type) || ['username', 'currentPassword', 'newPassword', 'confirmPassword', 'externalExerciseId', 'code', 'language', 'rendition', 'blobContainer', 'blobName', 'contentType', 'previewAsset'].includes(name) ? 'ltr' : 'auto';
  if (options.value !== undefined && options.value !== null) input.value = options.value;
  const hintId = options.hint ? id() : null;
  if (hintId) input.setAttribute('aria-describedby', hintId);
  const wrapper = el('div', { class: 'field' }, el('label', { for: inputId }, text), input, options.hint ? el('small', { id: hintId }, options.hint) : null);
  return { wrapper, input };
}
function passwordField(text, name, autocomplete = 'current-password') {
  const f = field(text, name, { type: 'password', autocomplete, maxlength: 128 });
  const toggle = button(t('password.show'), () => { const show = f.input.type === 'password'; f.input.type = show ? 'text' : 'password'; setText(toggle, show ? t('password.hide') : t('password.show')); toggle.setAttribute('aria-pressed', String(show)); bindAttribute(toggle, 'aria-label', t(show ? 'password.hideLabel' : 'password.showLabel', { field: text })); }, 'password-toggle', { 'aria-label': t('password.showLabel', { field: text }), 'aria-pressed': 'false', 'aria-controls': f.input.id });
  toggle.className = 'password-toggle';
  const wrap = el('div', { class: 'password-wrap' });
  f.input.replaceWith(wrap); wrap.append(f.input, toggle);
  return f;
}
function formValue(form) { return Object.fromEntries(new FormData(form).entries()); }
function modal(name, content, submitText, onSubmit, { wide = false } = {}) {
  const previous = document.activeElement;
  const headingId = id();
  const dialog = el('dialog', { 'aria-labelledby': headingId });
  if (wide) dialog.classList.add('wide');
  const form = el('form');
  const errors = el('div');
  const body = el('div', { class: 'dialog-body' }, errors, content);
  const close = () => { dialog.close(); };
  const save = submitText ? el('button', { type: 'submit', class: 'button primary' }, submitText) : null;
  form.append(el('div', { class: 'dialog-head' }, el('h2', { id: headingId }, name), button('×', close, 'icon quiet', { 'aria-label': t('dialog.closeLabel') })), body, el('div', { class: 'dialog-foot' }, button(submitText ? t('action.cancel') : t('action.close'), close, 'quiet'), save));
  form.addEventListener('submit', async event => {
    event.preventDefault(); if (!save) return; errors.replaceChildren(); save.disabled = true; setText(save, t('action.saving'));
    try { await onSubmit(form); close(); } catch (error) { errors.replaceChildren(errorBox(error)); errors.scrollIntoView({ block: 'nearest' }); } finally { save.disabled = false; setText(save, submitText); }
  });
  dialog.addEventListener('close', () => { dialog.remove(); if (previous?.isConnected) previous.focus(); }, { once: true });
  dialog.append(form); document.body.append(dialog); dialog.showModal();
  return { dialog, form, body, close };
}
function secretModal(username, temporaryPassword) {
  if (!temporaryPassword) { notify(t('user.saved')); return; }
  const secret = el('div', { class: 'secret', dir: 'ltr', tabindex: '0', 'aria-label': t('password.temporary') }, temporaryPassword);
  const status = el('p', { role: 'status', class: 'help-line' });
  const content = el('div', {}, el('p', {}, t('password.temporaryHint', { username })), secret, el('div', { class: 'actions space-top' }, button(t('password.copy'), async () => { try { await navigator.clipboard.writeText(secret.textContent); setText(status, t('password.copied')); } catch { setText(status, t('password.copyError')); } })), status);
  const m = modal(t('user.accessCreated'), content);
  m.dialog.addEventListener('close', () => { setText(secret, ''); temporaryPassword = null; });
}
function renderLogin(message) {
  shellCleanup(); shellCleanup = () => {};
  cleanupRoute();
  state.revision++; state.route = '';
  const username = field(t('field.username'), 'username', { autocomplete: 'username', maxlength: 128 });
  const password = passwordField(t('field.password'), 'password');
  const error = el('div', {}, message ? el('div', { class: 'info space-bottom' }, message) : null);
  const submit = el('button', { class: 'button primary', type: 'submit' }, t('login.submit'), el('span', { class: 'icon-directional', 'aria-hidden': 'true' }, '↗'));
  const form = el('form', { class: 'login-form' }, el('div', { class: 'login-logo-mobile' }, brand()), languagePicker(), el('div', { class: 'eyebrow' }, t('login.eyebrow')), el('h2', {}, t('login.title')), el('p', {}, t('login.description')), error, username.wrapper, password.wrapper, submit, el('div', { class: 'login-note' }, t('login.help')));
  form.addEventListener('submit', async event => {
    event.preventDefault(); submit.disabled = true; error.replaceChildren();
    try { await session(); await request('/portal/api/login', { method: 'POST', body: formValue(form) }); password.input.value = ''; await session(); renderShell(); } catch (e) { password.input.value = ''; error.replaceChildren(errorBox(e)); password.input.focus(); } finally { submit.disabled = false; }
  });
  app.replaceChildren(el('div', { class: 'login' }, el('aside', { class: 'login-story', 'aria-label': t('brand.accessible') }, brand(), el('div', { class: 'login-copy' }, el('div', { class: 'eyebrow' }, t('login.storyEyebrow')), el('h1', {}, t('login.storyFirst'), el('br'), el('em', {}, t('login.storySecond'))), el('p', {}, t('login.storyDescription'))), el('footer', {}, t('brand.platform'))), el('main', { class: 'login-main', id: 'main', tabindex: '-1' }, form)));
  setTitle(t('login.pageTitle'));
}
const adminNav = [['overview', 'overview', t('nav.overview')], ['videos', 'videos', t('nav.videos')], ['imports', 'videos', t('nav.imports')], ['partners', 'partners', t('nav.partners')], ['licenses', 'licenses', t('nav.licenses')], ['users', 'users', t('nav.users')], ['usage', 'usage', t('nav.usage')], ['settings', 'settings', t('nav.settings')]];
const partnerNav = [['catalogue', 'videos', t('nav.catalogue')], ['subpartners', 'partners', t('management.subpartners')], ['private-videos', 'videos', t('management.privateVideos')], ['documentation', 'documentation', t('nav.documentation')], ['usage', 'usage', t('nav.usage')], ['settings', 'settings', t('nav.settings')]];
function navigate(route) { if (location.hash.slice(1) === route) renderRoute(route); else location.hash = route; }
function renderShell(preferred) {
  if (!state.user) return renderLogin();
  shellCleanup(); shellCleanup = () => {};
  const navItems = state.user.role === 'admin' ? adminNav : partnerNav;
  const nav = el('nav', { class: 'nav', 'aria-label': t('nav.label') }, navItems.map(([route, icon, text]) => el('a', { href: `#${route}`, 'data-route': route, onclick: event => { if (event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) { event.preventDefault(); navigate(route); } } }, pixelIcon(icon), text)));
  const menu = button(pixelIcon('menu'), () => { const open = shell.classList.toggle('menu-open'); menu.setAttribute('aria-expanded', String(open)); }, 'icon quiet mobile-menu', { 'aria-label': t('nav.open'), 'aria-expanded': 'false', 'aria-controls': 'sidebar' });
  const shell = el('div', { class: 'shell' }, el('aside', { class: 'sidebar', id: 'sidebar' }, brand(), el('div', { class: 'nav-label' }, state.user.role === 'admin' ? t('nav.adminSection') : t('nav.partnerSection')), nav, el('div', { class: 'sidebar-foot' }, el('p', { class: 'eyebrow' }, t('nav.libraryTagline')), el('p', {}, t('nav.libraryDescription')))), el('div', { class: 'workarea' }, el('header', { class: 'topbar' }, menu, el('div', { class: 'context' }, t('brand.context')), languagePicker(), el('div', { class: 'identity' }, el('div', { class: 'avatar', dir: 'auto', 'aria-hidden': 'true' }, [...new Intl.Segmenter(getLocale(), { granularity: 'grapheme' }).segment(state.user.displayName || state.user.username)].slice(0, 2).map(part => part.segment).join('').toLocaleUpperCase(getLocale())), el('div', {}, el('div', { class: 'identity-name', dir: 'auto' }, state.user.displayName || state.user.username), el('div', { class: 'identity-role' }, labels[state.user.role])), button(t('auth.logout'), async event => {
    const logoutButton = event.currentTarget; logoutButton.disabled = true;
    try { await request('/portal/api/logout', { method: 'POST' }); state.user = null; state.csrf = ''; document.querySelectorAll('dialog').forEach(d => d.close()); renderLogin(); } catch (error) { logoutButton.disabled = false; notify(error.message); }
  }, 'small quiet'))), el('main', { class: 'main', id: 'main', tabindex: '-1' })));
  app.replaceChildren(shell);
  shellCleanup = observeHeaderHeight(shell);
  const requested = (preferred || location.hash.slice(1)).split('/')[0];
  renderRoute(state.user.mustChangePassword ? 'settings' : navItems.some(([r]) => r === requested) ? requested : navItems[0][0]);
}
function head(main, name, description, actions) {
  const navItems = state.user.role === 'admin' ? adminNav : partnerNav;
  const moduleIndex = Math.max(0, navItems.findIndex(([route]) => route === state.route));
  const moduleName = navItems[moduleIndex][2];
  main.append(el('div', { class: 'page-head' }, el('div', {}, el('div', { class: 'eyebrow' }, t('page.module', { index: padded(moduleIndex + 1), module: moduleName })), el('h1', { class: state.route === 'overview' ? 'hero-title' : null }, name), el('p', {}, description)), actions ? el('div', { class: 'actions' }, actions) : null));
}
function panel(name, body, action) { return el('section', { class: 'panel' }, name ? el('div', { class: 'panel-head' }, el('h2', {}, name), action) : null, body); }
function table(headers, rows, start = 0) {
  return el('div', { class: 'table-wrap', tabindex: '0', role: 'region', 'aria-label': list(headers) }, el('table', {}, el('thead', {}, el('tr', {}, el('th', { scope: 'col' }, t('table.rowNumber')), headers.map(h => el('th', { scope: 'col' }, h)))), el('tbody', {}, rows.map((row, index) => el('tr', {}, el('td', {}, el('span', { class: 'row-number' }, padded(start + index + 1))), row.map(cell => el('td', { dir: 'auto' }, cell)))))));
}
function pager(result, update) {
  const pages = Math.max(1, Math.ceil(result.total / result.pageSize));
  return el('div', { class: 'pager' }, el('span', {}, t('pager.summary', { count: result.total, page: number(result.page), pages: number(pages) })), el('div', { class: 'actions' }, button(t('pager.previous'), () => update(result.page - 1), 'small quiet', { disabled: result.page <= 1 }), button(t('pager.next'), () => update(result.page + 1), 'small quiet', { disabled: result.page >= pages })));
}
function paginated(main, path, headers, row, noItems, fetchPage) {
  const host = el('div'); main.append(host); const revision = state.revision;
  let sequence = 0;
  async function load(page = 1) {
    const current = ++sequence;
    host.replaceChildren(panel(null, loading()));
    try {
      const result = await (fetchPage ? fetchPage(page) : request(`${path}${path.includes('?') ? '&' : '?'}page=${page}&pageSize=25`));
      if (revision !== state.revision || current !== sequence) return;
      host.replaceChildren(panel(null, result.items.length ? el('div', {}, table(headers, result.items.map(row), (result.page - 1) * result.pageSize), pager(result, load)) : empty(noItems)));
    } catch (e) { if (revision === state.revision && current === sequence) host.replaceChildren(errorBox(e), button(t('action.retry'), () => load(page), 'quiet')); }
  }
  load(); return load;
}
async function renderRoute(route) {
  if (!state.user) return;
  const [routeName, section] = route.split('/');
  if (routeName === 'documentation') {
    route = routeName;
    if (section && state.route === route && $('#main .documentation') && !state.user.mustChangePassword) { focusDocumentationSection($('#main'), section); return; }
  }
  const navItems = state.user.role === 'admin' ? adminNav : partnerNav;
  if (!navItems.some(([r]) => r === route)) route = navItems[0][0];
  if (state.user.mustChangePassword) route = 'settings';
  cleanupRoute();
  state.route = route; const revision = ++state.revision;
  $('.shell')?.classList.remove('menu-open'); $('.mobile-menu')?.setAttribute('aria-expanded', 'false');
  document.querySelectorAll('[data-route]').forEach(a => { if (a.dataset.route === route) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current'); });
  const main = $('#main'); main.replaceChildren(); setTitle(t('page.title', { page: navItems.find(([r]) => r === route)[2] }));
  const views = { overview: overviewPage, videos: videosPage, imports: importsPage, partners: partnersPage, licenses: licensesPage, users: usersPage, catalogue: partnerCataloguePage, subpartners: management.partners, 'private-videos': management.uploads, documentation: main => renderDocumentationPage(main, { el, button, head }), usage: usagePage, settings: settingsPage };
  try { await views[route](main, revision); if (route === 'documentation') focusDocumentationSection(main, section || location.hash.split('/')[1]); } catch (e) { if (revision === state.revision) main.append(errorBox(e), button(t('action.retry'), () => renderRoute(route), 'quiet')); }
}
async function overviewPage(main, revision) {
  head(main, t('overview.title'), t('overview.description'));
  const host = loading(); main.append(host);
  const [partners, videos] = await Promise.all([request('/admin/v1/partners?pageSize=1'), request('/admin/v1/videos?pageSize=1')]);
  if (revision !== state.revision) return;
  host.replaceWith(statsPanel(t('overview.libraryFigures', { index: padded(1) }), [stat(t('overview.partnerCount'), partners.total, t('overview.allPartners')), stat(t('nav.videos'), videos.total, t('overview.allStatuses'))]));
  const modules = panel(t('overview.modules', { index: padded(2) }), el('div', { class: 'module-grid' },
    el('div', { class: 'module-cell' }, pixelIcon('videos'), el('div', { class: 'module-copy' }, el('div', { class: 'eyebrow' }, t('overview.libraryModule', { index: padded(1) })), el('h3', {}, t('nav.videos')), el('p', {}, t('overview.libraryHint'))), button(t('action.open'), () => navigate('videos'), 'navigation')),
    el('div', { class: 'module-cell' }, pixelIcon('licenses'), el('div', { class: 'module-copy' }, el('div', { class: 'eyebrow' }, t('overview.accessModule', { index: padded(2) })), el('h3', {}, t('nav.licenses')), el('p', {}, t('overview.accessHint'))), button(t('action.open'), () => navigate('licenses'), 'navigation'))));
  modules.classList.add('work-modules'); main.append(modules);
  main.append(el('p', { class: 'help-line' }, t('overview.realCounters')));
}
function stat(label, value, hint) { return el('div', { class: 'stat' }, el('p', {}, label), digits(value), hint ? el('small', {}, hint) : null); }
function videosPage(main) {
  let refresh;
  head(main, t('nav.videos'), t('videos.description'), [button(t('nav.imports'), () => navigate('imports'), 'quiet'), button(t('videos.createAction'), () => videoForm(null, () => refresh()), 'primary')]);
  const search = field(t('catalogue.searchLabel'), 'search', {
    type: 'search', required: false, maxlength: 512, placeholder: t('catalogue.searchPlaceholder')
  });
  let term = '';
  const form = el('form', { class: 'toolbar' }, search.wrapper,
    el('button', { type: 'submit', class: 'button small quiet' }, t('import.searchAction')));
  form.addEventListener('submit', event => {
    event.preventDefault();
    term = search.input.value.trim().normalize('NFC');
    refresh(1);
  });
  search.input.addEventListener('search', () => {
    if (!search.input.value && term) { term = ''; refresh(1); }
  });
  main.append(form);
  async function fetchPage(page) {
    if (!term) return request(`/admin/v1/videos?page=${page}&pageSize=25`);
    return request(`/admin/v1/videos/search?search=${encodeURIComponent(term)}&page=${page}&pageSize=25`);
  }
  refresh = paginated(main, '/admin/v1/videos', [t('field.exercise'), t('field.sourceId'), t('field.status'), t('field.updated'), t('field.actions')], video => [title(video.title), technical(video.externalExerciseId), badge(video.status), date(video.updatedAt), el('div', { class: 'actions' }, button(t('video.view'), () => previewVideo(video), 'small quiet'), button(t('video.files'), () => assetsPage(video), 'small quiet'), button(t('action.edit'), () => videoForm(video, () => refresh()), 'small quiet'))], t('videos.empty'), fetchPage);
}
const importStatus = { queued: t('import.status.queued'), running: t('import.status.running'), completed: t('import.status.completed'), failed: t('import.status.failed') };
const importStage = { queued: t('import.stage.queued'), probing: t('import.stage.probing'), downloading: t('import.stage.downloading'), transcoding: t('import.stage.transcoding'), uploading: t('import.stage.uploading'), completed: t('status.ready'), failed: t('import.stage.failed') };
async function importsPage(main, revision) {
  const lifetime = new AbortController();
  let timer, disposed = false, busy = false, page = 1, pendingPage = null, capabilities;
  let selected = 'running', search = '', previousRows = '', hasLoaded = false;
  const alive = () => !disposed && revision === state.revision && state.route === 'imports' && state.user?.role === 'admin';
  function cleanup() { disposed = true; clearTimeout(timer); lifetime.abort(); document.removeEventListener('visibilitychange', visibility); }
  routeCleanup = cleanup;
  function visibility() { clearTimeout(timer); if (!document.hidden && alive()) load(page); }
  document.addEventListener('visibilitychange', visibility);
  const create = button(t('import.create'), () => importForm(capabilities, () => load(1), alive), 'primary', { disabled: true });
  const refresh = button(t('action.refresh'), () => load(page), 'small quiet');
  const csvCreate = button(t('csv.title'), () => openCsvImport({ el, button, field, modal, technical, table, errorBox, request, alive, signal: lifetime.signal, onQueued: () => { if (alive()) load(1); } }), 'quiet', { disabled: true });
  head(main, t('nav.imports'), t('import.description'), [button(t('csv.download'), downloadCsvTemplate, 'quiet'), csvCreate, create]);
  const service = el('div', { class: 'space-bottom' }, loading(t('import.checkingService')));
  const errors = el('div', { class: 'page-error' });
  const host = el('div', {}, loading(t('import.loading')));
  const summary = el('p', { class: 'help-line', role: 'status' });
  const tabs = ['running', 'pending', 'completed'].map(filter => button(t(`import.filter.${filter}`), () => {
    if (filter === selected) return;
    selected = filter;
    tabs.forEach(tab => tab.setAttribute('aria-pressed', String(tab.dataset.importFilter === selected)));
    previousRows = ''; hasLoaded = false;
    errors.replaceChildren(); host.replaceChildren(loading(t('import.loading')));
    load(1);
  }, 'small quiet', { 'data-import-filter': filter, 'aria-pressed': String(filter === selected) }));
  const filterBar = el('div', { class: 'actions import-filters', role: 'group', 'aria-label': t('import.history'), hidden: true }, tabs);
  const searchField = field(t('import.searchLabel'), 'search', { type: 'search', maxlength: 512, placeholder: t('import.searchLabel') });
  searchField.wrapper.querySelector('label').classList.add('sr-only');
  const searchForm = el('form', { class: 'actions import-search', hidden: true }, searchField.wrapper,
    el('button', { type: 'submit', class: 'button small quiet' }, t('import.searchAction')));
  searchForm.addEventListener('submit', event => {
    event.preventDefault();
    const nextSearch = searchField.input.value.trim();
    if (nextSearch === search) return;
    search = nextSearch;
    previousRows = ''; hasLoaded = false;
    errors.replaceChildren(); host.replaceChildren(loading(t('import.loading')));
    load(1);
  });
  searchField.input.addEventListener('input', () => {
    if (!searchField.input.value && search) searchForm.requestSubmit();
  });
  main.append(service, panel(t('import.history'), el('div', {},
    el('div', { class: 'import-toolbar' }, filterBar, searchForm),
    summary, errors, host), refresh));
  try {
    capabilities = await request('/admin/v1/video-imports/capabilities', { signal: lifetime.signal });
    if (!alive()) return;
    filterBar.hidden = !capabilities.supportsStatusFilters;
    searchForm.hidden = !capabilities.supportsJobSearch;
    create.disabled = !capabilities.enabled;
    csvCreate.disabled = !capabilities.enabled;
    service.replaceChildren(el('div', { class: 'info' },
      el('p', {}, capabilities.enabled ? t('import.enabled') : t('import.disabled')),
      el('p', { class: 'help-line' }, t('import.limits', { minutes: number(capabilities.maxDurationSeconds / 60), size: number(capabilities.maxFileBytes / 1024 / 1024) }))));
  } catch (error) {
    if (!alive()) return;
    service.replaceChildren(errorBox(error), button(t('import.checkService'), () => renderRoute('imports'), 'small quiet'));
  }
  if (['localhost', '127.0.0.1'].includes(location.hostname)) {
    service.append(el('p', { class: 'help-line' },
      el('a', { href: 'http://127.0.0.1:5241/', target: '_blank', rel: 'noopener noreferrer' },
        'HEVC · 720p / 480p / 360p')));
  }
  if (alive()) await load();
  function schedule() { clearTimeout(timer); if (alive() && !document.hidden) timer = setTimeout(() => load(page), 5000); }
  async function load(nextPage = page) {
    if (!alive()) return;
    if (busy) { pendingPage = nextPage; return; }
    const filter = selected, term = search;
    busy = true; refresh.disabled = true; clearTimeout(timer);
    try {
      const result = await request(`/admin/v1/video-imports?page=${nextPage}&pageSize=25${capabilities?.supportsStatusFilters ? `&filter=${filter}` : ''}${term ? `&search=${encodeURIComponent(term)}` : ''}`, { signal: lifetime.signal });
      if (!alive() || selected !== filter || search !== term) return;
      const lastPage = Math.max(1, Math.ceil(result.total / result.pageSize));
      if (nextPage > lastPage) { pendingPage = lastPage; return; }
      page = result.page;
      errors.replaceChildren();
      setText(summary, t('import.filter.count', { count: number(result.total) }));
      let cacheReuse = {};
      if (['localhost', '127.0.0.1'].includes(location.hostname)) {
        try {
          const response = await fetch('/portal/local-hevc-reuse.json', {
            cache: 'no-store', signal: lifetime.signal
          });
          if (response.ok) cacheReuse = await response.json();
        } catch { /* Local diagnostics may be offline. */ }
      }
      if (!alive() || selected !== filter || search !== term) return;
      const fingerprint = JSON.stringify([result, result.items.map(job => cacheReuse[job.youtubeId])]);
      if (fingerprint !== previousRows) {
        const scrollLeft = $('.table-wrap', host)?.scrollLeft || 0;
        const focused = host.contains(document.activeElement) ? document.activeElement : null;
        const focusedJob = focused?.dataset.importId, focusedAction = focused?.dataset.importAction;
        host.replaceChildren(result.items.length ? el('div', {}, table([t('import.videoDate'), t('import.statusProgress'), t('import.resolutions'), t('field.actions')], result.items.map(job => importRow(job, cacheReuse[job.youtubeId])), (page - 1) * result.pageSize), pager(result, load)) : empty(t('import.empty')));
        if ($('.table-wrap', host)) $('.table-wrap', host).scrollLeft = scrollLeft;
        if (focusedJob) [...host.querySelectorAll('[data-import-id]')].find(node => node.dataset.importId === focusedJob && node.dataset.importAction === focusedAction)?.focus({ preventScroll: true });
        previousRows = fingerprint;
      }
      hasLoaded = true;
    } catch (error) {
      if (!alive() || selected !== filter || search !== term) return;
      errors.replaceChildren(errorBox(error));
      if (!hasLoaded) host.replaceChildren(empty(t('import.historyError')));
    } finally {
      busy = false;
      if (alive()) {
        refresh.disabled = false;
        if (pendingPage !== null) { const requestedPage = pendingPage; pendingPage = null; load(requestedPage); }
        else schedule();
      }
    }
  }
  function importRow(job, reusedHevc) {
    const percent = Math.max(0, Math.min(100, Math.round(Number(job.progressPercent) || 0)));
    const status = el('div', { class: 'import-status' }, el('span', { class: `badge ${job.status === 'completed' ? 'complete' : job.status === 'failed' ? 'danger' : 'pending'}` }, importStatus[job.status] || job.status), el('small', {}, t('import.progress', { stage: importStage[job.stage] || t('import.processing'), percent: percentage(percent / 100) })), el('progress', { class: 'meter', max: 100, value: percent, 'aria-label': t('import.progressLabel', { title: job.title || job.youtubeId || t('import.fallbackName') }) }));
    const localDiagnostics = ['localhost', '127.0.0.1'].includes(location.hostname);
    if (localDiagnostics && job.status === 'running') {
      const locale = getLocale().split('-')[0];
      const labels = {
        es: ['Descargando el original de YouTube', 'Original local · validando caché y preparando versiones', 'Original descargado · preparando versiones'],
        ca: ['Descarregant l’original de YouTube', 'Original local · validant la memòria cau i preparant versions', 'Original descarregat · preparant versions'],
        en: ['Downloading the YouTube source', 'Local source · checking cache and preparing versions', 'Source downloaded · preparing versions'],
        ar: ['جارٍ تنزيل الملف الأصلي من YouTube', 'ملف محلي · التحقق من ذاكرة التخزين المؤقت وإعداد النسخ', 'تم تنزيل الملف الأصلي · جارٍ إعداد النسخ']
      }[locale] || ['Downloading the YouTube source', 'Local source · checking cache and preparing versions', 'Source downloaded · preparing versions'];
      const detail = job.stage === 'downloading' ? labels[0]
        : job.stage === 'transcoding' ? labels[job.importOrigin === 'local_mp4' ? 1 : 2] : null;
      if (detail) status.append(el('small', { class: 'import-source' }, detail));
    }
    if (job.errorMessage) status.append(el('p', { class: 'import-error' }, job.errorMessage));
    const resolutionLabels = {
      es: ['En Azure', 'HEVC de caché', 'Pendiente', 'Preparación sin detalle'],
      ca: ['A Azure', 'HEVC de la memòria cau', 'Pendent', 'Preparació sense detall'],
      en: ['In Azure', 'Cached HEVC', 'Pending', 'Preparing · details unavailable'],
      ar: ['في Azure', 'HEVC من ذاكرة التخزين المؤقت', 'قيد الانتظار', 'جارٍ الإعداد دون تفاصيل']
    };
    const [inAzure, fromCache, pendingResolution, preparing] =
      resolutionLabels[getLocale().split('-')[0]] || resolutionLabels.en;
    const uploaded = new Set(job.completedHeights);
    const reused = new Set(job.importOrigin === 'local_mp4' && Array.isArray(reusedHevc) ? reusedHevc : []);
    const heights = [...new Set([...job.requestedHeights, ...uploaded])].sort((a, b) => b - a);
    const resolutions = el('div', {}, ...heights.map(height => {
      const labels = [];
      if (uploaded.has(height)) labels.push(inAzure);
      if (reused.has(height)) labels.push(fromCache);
      if (!labels.length) labels.push(job.stage === 'transcoding' ? preparing : pendingResolution);
      return el('div', {}, el('small', {}, `${height}p · ${labels.join(' · ')}`));
    }));
    const actions = el('div', { class: 'actions' });
    if (job.status === 'failed') actions.append(button(t('import.retry'), () => importForm(capabilities, () => load(1), alive, job), 'small quiet', { disabled: !capabilities?.enabled, 'data-import-id': job.id, 'data-import-action': 'retry' }));
    if (job.videoId) actions.append(button(t('import.viewExercise'), async event => {
      const target = event.currentTarget; target.disabled = true;
      try { const video = await request(`/admin/v1/videos/${job.videoId}`, { signal: lifetime.signal }); if (alive()) assetsPage(video); }
      catch (error) { if (alive()) errors.replaceChildren(errorBox(error)); }
      finally { if (target.isConnected) target.disabled = false; }
    }, 'small quiet', { 'data-import-id': job.id, 'data-import-action': 'exercise' }));
    const source = /^https:\/\/www\.youtube\.com\/watch\?v=[A-Za-z0-9_-]{11}$/.test(job.sourceUrl || '')
      ? el('a', { class: 'import-source', href: job.sourceUrl, target: '_blank', rel: 'noopener noreferrer', dir: 'ltr' }, job.sourceUrl)
      : job.sourceUrl ? el('small', { class: 'import-source', dir: 'ltr' }, job.sourceUrl) : null;
    const reference = job.reference ? el('small', { class: 'import-source' }, t('csv.reference'), ' · ', technical(job.reference)) : null;
    const origin = ['local_mp4', 'vivaldi', 'youtube'].includes(job.importOrigin) ? job.importOrigin : 'unknown';
    const originLabel = job.status === 'queued' && !job.importOrigin ? null
      : el('small', { class: 'import-source' }, t('import.origin.label'), ' · ', t(`import.origin.${origin === 'local_mp4' ? 'localMp4' : origin}`));
    const video = el('div', {}, title(job.title || job.youtubeId || t('import.youtubeVideo'), date(job.createdAt)), reference, source, originLabel);
    return [video, status, resolutions, actions];
  }
}
function importForm(capabilities, refresh, alive, previous) {
  if (!capabilities?.enabled || !alive()) return;
  const url = field(t('import.urlLabel'), 'url', { type: 'url', value: previous?.sourceUrl || '', maxlength: 2048, placeholder: 'https://www.youtube.com/watch?v=…', hint: t('import.urlHint') });
  const selected = new Set(previous?.requestedHeights || capabilities.defaultHeights);
  const choices = capabilities.availableHeights.map(height => {
    const check = el('input', { type: 'checkbox', name: 'height', value: height, checked: selected.has(height) });
    return el('label', { class: 'check-label' }, check, `${height}p`);
  });
  const resolutions = el('fieldset', { class: 'import-resolutions' }, el('legend', {}, t('import.mp4Versions')), choices);
  const view = modal(previous ? t('import.retryTitle') : t('import.create'), [url.wrapper, resolutions, el('div', { class: 'info space-top' }, t('import.backgroundHint')), el('p', { class: 'help-line' }, t('import.noUpscale'))], t('import.queue'), async () => {
    let parsed;
    try { parsed = new URL(url.input.value.trim()); } catch { throw new LocalizedError(t('import.invalidUrl')); }
    if (!['https:', 'http:'].includes(parsed.protocol)) throw new LocalizedError(t('import.invalidProtocol'));
    const heights = [...view.form.querySelectorAll('input[name="height"]:checked')].map(input => Number(input.value));
    if (!heights.length) throw new LocalizedError(t('import.selectResolution'));
    await request('/admin/v1/video-imports', { method: 'POST', body: { url: parsed.href, heights } });
    if (alive()) { notify(t('import.queuedNotice')); refresh(); }
  });
}
function videoForm(video, refresh) {
  const titleField = field(t('field.title'), 'title', { value: video?.title, maxlength: 300 });
  const external = field(t('video.sourceIdLabel'), 'externalExerciseId', { value: video?.externalExerciseId, maxlength: 128, hint: t('video.sourceIdHint') });
  const status = field(t('field.status'), 'status', { choices: ['draft', 'active', 'archived'], value: video?.status || 'draft' });
  modal(video ? t('video.editTitle') : t('video.createTitle'), [titleField.wrapper, external.wrapper, status.wrapper], t('video.save'), async form => { await request(`/admin/v1/videos${video ? `/${video.id}` : ''}`, { method: video ? 'PUT' : 'POST', body: formValue(form) }); notify(t('video.saved')); refresh(); });
}
async function assetsPage(video) {
  cleanupRoute();
  const revision = ++state.revision; const main = $('#main'); main.replaceChildren();
  head(main, video.title, t('video.sourceId', { sourceId: video.externalExerciseId }), [button(t('video.back'), () => renderRoute('videos'), 'quiet'), button(t('asset.registerExisting'), () => assetForm(video, null, () => assetsPage(video)), 'quiet'), button(t('upload.create'), () => uploadVideo(video, () => assetsPage(video)), 'primary')]);
  main.append(el('div', { class: 'info space-bottom' }, t('asset.description')));
  if (revision !== state.revision) return;
  paginated(main, `/admin/v1/videos/${video.id}/assets`, [t('field.file'), t('asset.languageFormat'), t('field.duration'), t('field.size'), t('field.status'), t('field.actions')], asset => [title(technical(asset.blobName), technical(asset.blobContainer)), t('asset.languageRendition', { language: asset.language, rendition: asset.rendition }), t('asset.seconds', { value: number(asset.durationMs / 1000) }), t('asset.byteSize', { value: number(asset.sizeBytes) }), badge(asset.status), el('div', { class: 'actions' }, button(t('video.view'), () => previewVideo(video, asset), 'small quiet', { disabled: asset.status !== 'ready', title: asset.status === 'ready' ? t('preview.open') : t('preview.requiresReady') }), button(t('action.edit'), () => assetForm(video, asset, () => assetsPage(video)), 'small quiet'))], t('asset.empty'));
}
function sendVideoFile(videoId, data, progress, signal) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    if (signal.aborted) { reject(new DOMException(t('upload.cancelled'), 'AbortError')); return; }
    xhr.open('POST', `/admin/v1/videos/${videoId}/assets/upload`);
    xhr.withCredentials = true;
    xhr.responseType = 'json';
    xhr.timeout = 11 * 60 * 1000;
    xhr.setRequestHeader('Accept', 'application/json');
    xhr.setRequestHeader('Accept-Language', getLocale());
    xhr.setRequestHeader('X-CSRF-TOKEN', state.csrf);
    xhr.upload.addEventListener('progress', event => { if (event.lengthComputable) progress(event.loaded, event.total); });
    xhr.addEventListener('load', () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve(xhr.response);
      else {
        const problem = xhr.response || {};
        const message = xhr.status === 503 ? t('upload.azureError') : xhr.status === 413 ? t('upload.tooLarge') : problem.detail || problem.title || t('upload.httpError', { status: number(xhr.status) });
        reject(new ApiError(message, xhr.status, problem.requestId || problem.extensions?.requestId || xhr.getResponseHeader('X-Request-ID')));
      }
    });
    xhr.addEventListener('error', () => reject(new LocalizedError(t('upload.networkError'))));
    xhr.addEventListener('timeout', () => reject(new LocalizedError(t('upload.timeout'))));
    xhr.addEventListener('abort', () => reject(new DOMException(t('upload.cancelled'), 'AbortError')));
    xhr.addEventListener('loadend', () => signal.removeEventListener('abort', abort));
    signal.addEventListener('abort', abort, { once: true });
    xhr.send(data);
  });
}
function uploadVideo(video, refresh) {
  const file = field(t('upload.fileLabel'), 'file', { type: 'file', hint: t('upload.fileHint') });
  file.input.classList.add('sr-only');
  const filename = el('span', { class: 'file-name', dir: 'auto', role: 'status' }, t('upload.noFile'));
  file.wrapper.append(el('div', { class: 'file-chooser' }, button(t('upload.chooseFile'), () => file.input.click(), 'quiet'), filename));
  file.input.addEventListener('change', () => setText(filename, file.input.files?.[0]?.name || t('upload.noFile')));
  file.input.accept = 'video/mp4,.mp4';
  const language = field(t('field.language'), 'language', { value: 'es', maxlength: 35, pattern: '[a-z]{2,3}(-[A-Za-z0-9]{2,8})*' });
  const rendition = field(t('field.rendition'), 'rendition', { value: '720p', maxlength: 50, pattern: '[A-Za-z0-9_-]+', hint: t('upload.renditionHint') });
  const metadata = el('p', { class: 'help-line', role: 'status' }, t('upload.selectHint'));
  const mediaHost = el('div');
  const progress = el('progress', { class: 'upload-progress', max: 100, value: 0, 'aria-label': t('upload.progressLabel'), hidden: true });
  const status = el('p', { class: 'help-line', role: 'status' });
  const lifetime = new AbortController();
  let objectUrl;
  let media;
  let durationMs = 0;
  let busy = false;
  let metadataTimer;
  function releaseMedia() {
    clearTimeout(metadataTimer);
    if (media) { media.pause(); media.removeAttribute('src'); media.load(); media = null; }
    if (objectUrl) { URL.revokeObjectURL(objectUrl); objectUrl = null; }
    mediaHost.replaceChildren();
  }
  const view = modal(t('upload.title', { title: video.title }), [file.wrapper, el('div', { class: 'upload-fields' }, language.wrapper, rendition.wrapper), metadata, mediaHost, progress, status], t('upload.submit'), async () => {
    const selected = file.input.files?.[0];
    if (!selected || !durationMs) throw new LocalizedError(t('upload.waitMetadata'));
    const data = new FormData();
    data.set('language', language.input.value);
    data.set('rendition', rendition.input.value);
    data.set('durationMs', String(durationMs));
    data.set('file', selected);
    busy = true;
    for (const input of [file.input, language.input, rendition.input]) input.disabled = true;
    progress.hidden = false; progress.value = 0;
    setText(status, t('upload.sending'));
    try {
      await sendVideoFile(video.id, data, (loaded, total) => {
        progress.value = Math.min(100, Math.round(loaded / total * 100));
        setText(status, progress.value === 100 ? t('upload.persisting') : t('upload.percent', { percent: percentage(progress.value / 100) }));
      }, lifetime.signal);
      notify(t('upload.success'));
      refresh();
    } catch (error) {
      progress.hidden = true;
      setText(status, t('upload.reviewWarning'));
      throw error;
    } finally {
      busy = false;
      for (const input of [file.input, language.input, rendition.input]) input.disabled = false;
    }
  }, { wide: true });
  const submit = view.form.querySelector('button[type="submit"]');
  submit.disabled = true;
  file.input.addEventListener('change', () => {
    durationMs = 0; submit.disabled = true; setText(status, ''); progress.hidden = true;
    releaseMedia();
    const selected = file.input.files?.[0];
    if (!selected) { setText(metadata, t('upload.selectHint')); return; }
    if (!/\.mp4$/i.test(selected.name) || selected.size === 0 || selected.size > 200 * 1024 * 1024) {
      metadata.replaceChildren(errorBox(new LocalizedError(t('upload.invalidFile'))));
      return;
    }
    setText(metadata, t('upload.checking'));
    const candidate = el('video', { class: 'preview-player', controls: true, playsinline: true, preload: 'metadata', 'aria-label': t('upload.previewLabel') });
    media = candidate;
    function invalid() {
      if (media !== candidate) return;
      durationMs = 0; submit.disabled = true;
      metadata.replaceChildren(errorBox(new LocalizedError(t('upload.unreadable'))));
    }
    candidate.addEventListener('loadedmetadata', () => {
      if (media !== candidate) return;
      clearTimeout(metadataTimer);
      const measured = Math.round(candidate.duration * 1000);
      if (!Number.isFinite(measured) || measured < 1 || measured > 2147483647 || !candidate.videoHeight) { invalid(); return; }
      durationMs = measured;
      rendition.input.value = `${candidate.videoHeight}p`;
      setText(metadata, t('upload.metadata', { size: number(selected.size / 1024 / 1024), duration: number(measured / 1000), width: number(candidate.videoWidth), height: number(candidate.videoHeight) }));
      submit.disabled = false;
    }, { once: true });
    candidate.addEventListener('error', invalid);
    objectUrl = URL.createObjectURL(selected);
    candidate.src = objectUrl;
    mediaHost.append(candidate);
    metadataTimer = setTimeout(invalid, 15000);
  });
  view.dialog.addEventListener('close', () => {
    lifetime.abort(); releaseMedia();
    if (busy) { notify(t('upload.interrupted')); refresh(); }
  });
}
async function previewVideo(video, selectedAsset) {
  const host = el('div', {}, loading(t('preview.loadingAssets')));
  const view = modal(video.title, host, null, null, { wide: true });
  const lifetime = new AbortController();
  let playbackRequest;
  let player;
  let generation = 0;
  let expiryTimer;
  function stopPlayer() {
    clearTimeout(expiryTimer);
    if (player) { player.pause(); player.removeAttribute('src'); player.load(); player = null; }
  }
  view.dialog.addEventListener('close', () => { lifetime.abort(); playbackRequest?.abort(); stopPlayer(); });
  try {
    const assets = selectedAsset ? [selectedAsset] : await all(`/admin/v1/videos/${video.id}/assets`);
    if (!view.dialog.open) return;
    const ready = assets.filter(asset => asset.status === 'ready');
    if (!ready.length) {
      host.replaceChildren(empty(t('preview.empty')));
      return;
    }
    const select = field(t('preview.assetLabel'), 'previewAsset', { choices: ready.map(asset => [asset.id, t('asset.previewOption', { language: asset.language, rendition: asset.rendition, duration: number(asset.durationMs / 1000) })]) });
    const output = el('div', { class: 'preview-output', 'aria-live': 'polite' });
    const retry = button(t('preview.renew'), () => play(select.input.value), 'quiet');
    host.replaceChildren(select.wrapper, output, el('div', { class: 'actions space-top' }, retry), el('p', { class: 'help-line space-top' }, t('preview.hint')));
    select.input.addEventListener('change', () => play(select.input.value));
    async function play(assetId) {
      const current = ++generation;
      playbackRequest?.abort();
      playbackRequest = new AbortController();
      stopPlayer();
      output.replaceChildren(loading(t('preview.preparing')));
      retry.disabled = true;
      try {
        const result = await request(`/admin/v1/videos/${video.id}/assets/${assetId}/preview`, { method: 'POST', signal: playbackRequest.signal });
        if (!view.dialog.open || current !== generation) return;
        const media = el('video', { class: 'preview-player', controls: true, playsinline: true, preload: 'metadata', 'aria-label': t('preview.videoLabel', { title: video.title }) });
        player = media;
        const status = el('p', { class: 'help-line' }, t('preview.accessExpiry', { date: date(result.expiresAt) }));
        media.addEventListener('error', () => { status.replaceChildren(errorBox(new LocalizedError(t('preview.loadError')))); });
        media.src = result.url;
        output.replaceChildren(media, status);
        expiryTimer = setTimeout(() => {
          if (!view.dialog.open || current !== generation) return;
          stopPlayer();
          output.replaceChildren(el('div', { class: 'info', role: 'status' }, t('preview.expired')));
        }, Math.max(0, new Date(result.expiresAt).getTime() - Date.now()));
      } catch (error) {
        if (view.dialog.open && current === generation && error.name !== 'AbortError') {
          const message = error.status === 503 ? new ApiError(t('preview.unavailable'), error.status, error.requestId) : error;
          output.replaceChildren(errorBox(message));
        }
      } finally { if (current === generation) retry.disabled = false; }
    }
    await play(select.input.value);
  } catch (error) {
    if (view.dialog.open && !lifetime.signal.aborted) host.replaceChildren(errorBox(error));
  }
}
function assetForm(video, asset, refresh) {
  const inputs = [field(t('field.language'), 'language', { value: asset?.language || 'es', maxlength: 35, hint: t('asset.languageHint') }), field(t('asset.renditionLabel'), 'rendition', { value: asset?.rendition || '720p', maxlength: 50 }), field(t('asset.containerLabel'), 'blobContainer', { value: asset?.blobContainer || 'video', maxlength: 63 }), field(t('asset.pathLabel'), 'blobName', { value: asset?.blobName, maxlength: 1024, placeholder: 'ejercicios/sentadilla/es-720p.mp4', hint: t('asset.pathHint') }), field(t('asset.contentType'), 'contentType', { choices: [['video/mp4', t('upload.fileLabel')]], value: 'video/mp4' }), field(t('asset.bytes'), 'sizeBytes', { type: 'number', min: 0, step: 1, value: asset?.sizeBytes || 0 }), field(t('asset.milliseconds'), 'durationMs', { type: 'number', min: 0, max: 2147483647, step: 1, value: asset?.durationMs || 0, hint: t('asset.durationHint') }), field(t('field.status'), 'status', { choices: ['pending', 'ready', 'disabled'], value: asset?.status || 'pending' })];
  modal(asset ? t('asset.editTitle') : t('asset.createTitle'), inputs.map(f => f.wrapper), t('asset.save'), async form => { const data = formValue(form); data.sizeBytes = Number(data.sizeBytes); data.durationMs = Number(data.durationMs); await request(`/admin/v1/videos/${video.id}/assets${asset ? `/${asset.id}` : ''}`, { method: asset ? 'PUT' : 'POST', body: data }); notify(t('asset.saved')); refresh(); });
}
function partnersPage(main) {
  let refresh;
  head(main, t('nav.partners'), t('partner.description'), button(t('partner.createAction'), () => partnerForm(null, () => refresh()), 'primary'));
  refresh = paginated(main, '/admin/v1/partners', [t('role.partner'), t('field.code'), t('field.status'), t('field.created'), t('field.actions')], partner => [title(partner.name), technical(partner.code), badge(partner.status), date(partner.createdAt), el('div', { class: 'actions' }, button(t('field.licenses'), () => { state.partnerId = partner.id; navigate('licenses'); }, 'small quiet'), button(t('action.edit'), () => partnerForm(partner, () => refresh()), 'small quiet'))], t('partner.empty'));
}
function partnerForm(partner, refresh) {
  const fields = [field(t('partner.nameLabel'), 'name', { value: partner?.name, maxlength: 200 }), field(t('field.code'), 'code', { value: partner?.code, maxlength: 64, pattern: '[a-z0-9][a-z0-9_\\-]*', hint: t('partner.codeHint') }), field(t('field.status'), 'status', { choices: ['active', 'suspended', 'revoked'], value: partner?.status || 'active' })];
  modal(partner ? t('partner.editTitle') : t('partner.createTitle'), fields.map(f => f.wrapper), t('partner.save'), async form => { await request(`/admin/v1/partners${partner ? `/${partner.id}` : ''}`, { method: partner ? 'PUT' : 'POST', body: formValue(form) }); notify(t('partner.saved')); refresh(); });
}
async function partnerSelector(main, onChange, revision) {
  const loadingNode = loading(t('partner.loading')); main.append(loadingNode);
  const partners = await all('/admin/v1/partners'); if (revision !== state.revision) return null;
  loadingNode.remove();
  if (!partners.length) { main.append(panel(null, empty(t('partner.required'), button(t('partner.goTo'), () => navigate('partners'), 'primary')))); return null; }
  if (!partners.some(p => p.id === state.partnerId)) state.partnerId = partners[0].id;
  const select = field(t('role.partner'), 'partner', { choices: partners.map(p => [p.id, `${p.name} · ${p.code}`]), value: state.partnerId });
  select.input.addEventListener('change', () => { state.partnerId = select.input.value; onChange(); });
  main.append(el('div', { class: 'toolbar' }, select.wrapper));
  return partners;
}
async function licensesPage(main, revision) {
  let refresh;
  const create = button(t('license.createAction'), () => licenseForm(null, state.partnerId, () => refresh()), 'primary', { disabled: true });
  head(main, t('nav.licenses'), t('license.description'), create);
  if (!await partnerSelector(main, () => renderRoute('licenses'), revision)) return;
  create.disabled = false;
  main.append(el('p', { class: 'help-line space-bottom' }, t('license.dateHint')));
  const partnerId = state.partnerId;
  refresh = paginated(main, `/admin/v1/partners/${partnerId}/licenses`, [t('field.license'), t('field.status'), t('license.localValidity'), t('license.sessionsMonth'), t('field.actions')], license => [title(license.name), badge(license.status), title(date(license.startsAt), t('license.until', { date: date(license.endsAt) })), license.unlimitedSessions ? t('license.unlimited') : number(license.monthlySessionQuota), el('div', { class: 'actions' }, button(t('field.catalogue'), () => catalogueEditor(partnerId, license), 'small quiet'), button(t('action.edit'), () => licenseForm(license, partnerId, () => refresh()), 'small quiet'))], t('license.empty'));
}
function localDate(value) { const d = new Date(value); return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16); }
function licenseForm(license, partnerId, refresh, {
  path = `/admin/v1/partners/${partnerId}/licenses`, allowManagement = true, allowUploads = true
} = {}) {
  const now = new Date(); const nextYear = new Date(now); nextYear.setFullYear(now.getFullYear() + 1);
  const mode = field(t('license.modeLabel'), 'unlimitedSessions', { choices: [['false', t('license.monthlyQuota')], ['true', t('license.unlimitedSessions')]], value: String(Boolean(license?.unlimitedSessions)), hint: t('license.unlimitedHint') });
  const quota = field(t('license.quotaLabel'), 'monthlySessionQuota', { type: 'number', min: 0, step: 1, value: license?.monthlySessionQuota ?? 1000, hint: t('license.zeroHint') });
  function updateQuota() {
    const unlimited = mode.input.value === 'true';
    quota.wrapper.hidden = unlimited;
    quota.input.disabled = unlimited;
    quota.input.required = !unlimited;
  }
  mode.input.addEventListener('change', updateQuota);
  updateQuota();
  const fields = [field(t('field.name'), 'name', { value: license?.name, maxlength: 200 }), field(t('field.status'), 'status', { choices: ['active', 'suspended', 'revoked'], value: license?.status || 'active' }), field(t('license.startLabel'), 'startsAt', { type: 'datetime-local', value: localDate(license?.startsAt || now) }), field(t('license.endLabel'), 'endsAt', { type: 'datetime-local', value: localDate(license?.endsAt || nextYear) }), mode, quota];
  const flag = (key, name) => field(t(key), name, { choices: [['false', t('status.disabled')], ['true', t('status.active')]], value: String(Boolean(license?.[name])) });
  if (allowManagement) fields.push(flag('management.allowManagement', 'allowSubpartnerManagement'), flag('management.allowDelegation', 'allowCatalogueDelegation'));
  if (allowUploads) {
    const enabled = flag('management.privateUploads', 'allowPartnerUploads');
    const uploadMode = field(t('management.uploadQuota'), 'uploadQuotaMode', {
      choices: [['unlimited', t('license.unlimited')], ['storage', t('management.quotaStorage')], ['videos', t('management.quotaVideos')]],
      value: license?.uploadQuotaMode || 'unlimited'
    });
    const limit = field(t('management.quotaLimit'), 'uploadQuotaLimit', {
      type: 'number', required: false, value: license?.uploadQuotaMode === 'storage' ? license.uploadQuotaLimit / 1_000_000_000 : license?.uploadQuotaLimit
    });
    function updateUploadQuota() {
      const on = enabled.input.value === 'true';
      const limited = on && uploadMode.input.value !== 'unlimited';
      uploadMode.wrapper.hidden = uploadMode.input.disabled = !on;
      limit.wrapper.hidden = limit.input.disabled = !limited;
      limit.input.required = limited;
      const storage = uploadMode.input.value === 'storage';
      limit.input.min = storage ? '0.000000001' : '1';
      limit.input.step = storage ? '0.000000001' : '1';
    }
    enabled.input.addEventListener('change', updateUploadQuota);
    uploadMode.input.addEventListener('change', () => { limit.input.value = ''; updateUploadQuota(); });
    updateUploadQuota();
    fields.push(enabled, uploadMode, limit);
  }
  modal(license ? t('license.editTitle') : t('license.createTitle'), [el('div', { class: 'info' }, t('license.timeZoneHint')), ...fields.map(f => f.wrapper)], t('license.save'), async form => {
    const data = formValue(form); data.unlimitedSessions = data.unlimitedSessions === 'true'; data.monthlySessionQuota = data.unlimitedSessions ? 0 : Number(data.monthlySessionQuota); data.startsAt = new Date(data.startsAt).toISOString(); data.endsAt = new Date(data.endsAt).toISOString();
    if (data.endsAt <= data.startsAt) throw new LocalizedError(t('license.invalidEnd'));
    for (const name of ['allowSubpartnerManagement', 'allowCatalogueDelegation', 'allowPartnerUploads']) {
      if (data[name] !== undefined) data[name] = data[name] === 'true';
    }
    if (data.uploadQuotaLimit !== undefined) {
      data.uploadQuotaLimit = Math.round(Number(data.uploadQuotaLimit) * (data.uploadQuotaMode === 'storage' ? 1_000_000_000 : 1));
      if (!Number.isSafeInteger(data.uploadQuotaLimit) || data.uploadQuotaLimit <= 0) throw new LocalizedError(t('validation.value'));
    }
    await request(`${path}${license ? `/${license.id}` : ''}`, { method: license ? 'PUT' : 'POST', body: data }); notify(t('license.saved')); refresh();
  });
}
async function catalogueEditor(partnerId, license) {
  const revision = ++state.revision; const main = $('#main'); main.replaceChildren();
  head(main, t('catalogue.title', { name: license.name }), t('catalogue.description'), button(t('catalogue.back'), () => leave(), 'quiet'));
  let dirty = false;
  function leave() { if (!dirty) renderRoute('licenses'); else modal(t('catalogue.unsavedTitle'), el('p', {}, t('catalogue.discardWarning')), t('catalogue.discard'), async () => { dirty = false; renderRoute('licenses'); }); }
  const wait = loading(t('catalogue.loading')); main.append(wait);
  const path = `/admin/v1/partners/${partnerId}/licenses/${license.id}/catalogue`;
  const videosPath = '/admin/v1/videos';
  const pageSize = 30;
  let firstPage, catalogue;
  try {
    [firstPage, catalogue] = await Promise.all([request(`${videosPath}?page=1&pageSize=${pageSize}`), request(path)]);
  } catch (error) {
    if (revision === state.revision) wait.replaceWith(errorBox(error));
    return;
  }
  let requestVersion = 0;
  if (revision !== state.revision) return; wait.remove();
  let selected = new Set(catalogue.videoIds); let baseline = new Set(catalogue.videoIds); let query = ''; let page = 1;
  const search = field(t('catalogue.searchLabel'), 'search', { type: 'search', required: false, placeholder: t('catalogue.searchPlaceholder') }); search.wrapper.classList.add('search');
  const count = el('p', { class: 'help-line', role: 'status' }); const changes = el('span', { class: 'dirty' });
  const errors = el('div', { class: 'page-error' }); const list = el('div');
  const save = button(t('catalogue.save'), () => confirmSave(), 'primary');
  const selectionInfo = el('div', { class: 'catalogue-summary' }, el('div', {}, count, changes), el('div', { class: 'actions' }, button(t('catalogue.clear'), () => { selected.clear(); updateDirty(); draw(); }, 'small quiet'), save));
  main.append(el('div', { class: 'info space-bottom' }, t('catalogue.saveHint')), errors, el('div', { class: 'toolbar' }, search.wrapper), panel(null, el('div', { class: 'panel-body' }, selectionInfo)), list);
  function updateDirty() { dirty = selected.size !== baseline.size || [...selected].some(value => !baseline.has(value)); setText(changes, dirty ? t('catalogue.unsaved') : t('catalogue.selectionSaved')); setText(count, t('catalogue.selectionCount', { count: selected.size, total: number(firstPage.total) })); save.disabled = !dirty || selected.size > 1000; }
  async function draw() {
    const version = ++requestVersion;
    const searchTerm = query;
    let items, total;
    try {
      const result = searchTerm
        ? await request(`${videosPath}/search?search=${encodeURIComponent(searchTerm)}&page=${page}&pageSize=${pageSize}`)
        : page === 1 ? firstPage : await request(`${videosPath}?page=${page}&pageSize=${pageSize}`);
      if (version !== requestVersion || revision !== state.revision) return;
      ({ items, total } = result);
      errors.replaceChildren();
    } catch (error) {
      if (version === requestVersion && revision === state.revision) errors.replaceChildren(errorBox(error));
      return;
    }
    const rows = items.map(v => {
      const check = el('input', { type: 'checkbox', checked: selected.has(v.id), 'aria-label': t('catalogue.include', { title: v.title }), onchange: () => { if (check.checked) selected.add(v.id); else selected.delete(v.id); updateDirty(); } });
      return el('div', { class: 'catalogue-row' },
        el('label', { class: 'catalogue-selection' }, check, title(v.title, v.externalExerciseId)),
        badge(v.status), button(t('video.view'), () => previewVideo(v), 'small quiet'));
    });
    list.replaceChildren(panel(null, rows.length ? el('div', {}, el('div', { class: 'catalogue-list' }, rows), pager({ total, page, pageSize }, p => { page = p; draw(); })) : empty(t('catalogue.noMatches'))));
    updateDirty();
  }
  async function commit() {
    save.disabled = true; errors.replaceChildren();
    try { await request(path, { method: 'PUT', body: { videoIds: [...selected] } }); baseline = new Set(selected); dirty = false; notify(t('catalogue.saved')); } catch (e) { errors.replaceChildren(errorBox(e)); throw e; } finally { updateDirty(); }
  }
  function confirmSave() {
    if (selected.size === 0) modal(t('catalogue.clearTitle'), el('p', {}, t('catalogue.clearWarning')), t('catalogue.saveEmpty'), commit);
    else modal(t('catalogue.confirmTitle'), el('p', {}, t('catalogue.confirm', { name: license.name, count: selected.size })), t('catalogue.saveSelection'), commit);
  }
  let searchTimer;
  search.input.addEventListener('input', () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      if (revision !== state.revision) return;
      query = search.input.value.normalize('NFC').trim();
      page = 1; draw();
    }, 300);
  });
  draw();
}
const management = managementViews({ el, button, head, panel, table, empty, loading, errorBox, field, modal, request,
  all, paginated, badge, title, technical, notify, licenseForm, LocalizedError, pager, formValue,
  isCurrent: revision => revision === state.revision, currentRevision: () => state.revision });

function partnerCataloguePage(main) {
  head(main, t('nav.catalogue'), t('catalogue.partnerDescription'));
  paginated(main, '/v1/videos', [t('field.exercise'), t('field.sourceId'), t('catalogue.availableFiles'), t('field.actions')], video => [title(video.title), technical(video.externalExerciseId), t('catalogue.assetCount', { count: video.assets.length, languages: list([...new Set(video.assets.map(a => a.language))]) }), button(t('catalogue.details'), () => {
    modal(video.title, [el('p', {}, t('video.sourceId', { sourceId: video.externalExerciseId })), video.assets.length ? table([t('field.language'), t('field.format'), t('field.duration'), t('field.size')], video.assets.map(a => [a.language, a.rendition, t('asset.seconds', { value: number(a.durationMs / 1000) }), t('asset.byteSize', { value: number(a.sizeBytes) })])) : empty(t('catalogue.noFiles')), el('p', { class: 'help-line space-top' }, t('catalogue.detailsHint'))], null, null, { wide: true });
  }, 'small quiet')], t('catalogue.partnerEmpty'));
}
async function usersPage(main, revision) {
  let refresh;
  const create = button(t('user.createAction'), () => userForm(null, partners, () => refresh()), 'primary', { disabled: true });
  head(main, t('nav.users'), t('user.description'), create);
  const partners = await all('/admin/v1/partners'); if (revision !== state.revision) return; create.disabled = false;
  const partnerNames = new Map(partners.map(p => [p.id, p.name]));
  refresh = paginated(main, '/portal/api/users', [t('field.username'), t('user.roleOrganisation'), t('user.access'), t('field.actions')], user => [title(user.displayName, technical(user.username)), title(labels[user.role], user.partnerId ? partnerNames.get(user.partnerId) || user.partnerId : 'FYXBODY'), el('div', {}, badge(user.isActive ? 'active' : 'disabled'), user.mustChangePassword ? el('small', {}, t('user.mustChangePassword')) : null), el('div', { class: 'actions' }, button(t('action.edit'), () => userForm(user, partners, () => refresh()), 'small quiet'), button(t('user.resetAction'), () => resetPassword(user, () => refresh()), 'small quiet'))], t('user.empty'));
}
function userForm(user, partners, refresh) {
  const display = field(t('user.displayName'), 'displayName', { value: user?.displayName, maxlength: 200 });
  const contents = [display.wrapper];
  let active;
  if (!user) {
    const username = field(t('field.username'), 'username', { maxlength: 128, pattern: '[A-Za-z0-9@._+\\-]{3,128}', autocomplete: 'off', hint: t('user.usernameHint') });
    const role = field(t('user.roleLabel'), 'role', { choices: [['partner', t('role.partner')], ['admin', t('role.admin')]], value: 'partner' });
    const partner = field(t('user.organisation'), 'partnerId', { choices: [['', t('user.selectOrganisation')], ...partners.map(p => [p.id, p.name])] });
    role.input.addEventListener('change', () => { const isPartner = role.input.value === 'partner'; partner.wrapper.hidden = !isPartner; partner.input.disabled = !isPartner; partner.input.required = isPartner; });
    contents.unshift(username.wrapper); contents.push(role.wrapper, partner.wrapper, el('div', { class: 'info space-top' }, t('user.temporaryHint')));
  } else { active = el('input', { type: 'checkbox', name: 'isActive', checked: user.isActive }); contents.push(el('label', { class: 'check-label space-top' }, active, t('user.activeAccess')), el('p', { class: 'help-line space-top' }, t('user.identityHint', { username: user.username, role: labels[user.role] }))); }
  modal(user ? t('user.editTitle') : t('user.createTitle'), contents, t('user.save'), async form => {
    const data = formValue(form);
    if (user) { data.isActive = active.checked; await request(`/portal/api/users/${user.id}`, { method: 'PUT', body: data }); notify(t('user.saved')); refresh(); }
    else { if (data.role === 'admin') data.partnerId = null; const result = await request('/portal/api/users', { method: 'POST', body: data }); refresh(); setTimeout(() => secretModal(result.user.username, result.temporaryPassword), 0); }
  });
}
function resetPassword(user, refresh) {
  modal(t('user.resetPassword'), el('p', {}, t('user.resetWarning', { username: user.username })), t('user.resetPassword'), async () => { const result = await request(`/portal/api/users/${user.id}/reset-password`, { method: 'POST' }); refresh(); setTimeout(() => secretModal(result.user.username, result.temporaryPassword), 0); });
}
async function usagePage(main, revision) {
  head(main, t('nav.usage'), t('usage.description'));
  const admin = state.user.role === 'admin';
  if (admin && !await partnerSelector(main, () => renderRoute('usage'), revision)) return;
  if (revision !== state.revision) return;
  const period = parseUsagePeriod(state.month);
  const month = field(t('usage.monthLabel'), 'month', { value: period.month, choices: usageMonthIds.map(value => [value, new LocalizedValue(() => formatUsageMonth(getLocale(), value))]) });
  const year = field(t('usage.yearLabel'), 'usageYear', { type: 'number', value: period.year, min: 1, max: 9998, step: 1 });
  const host = el('div'); const errors = el('div');
  const csv = admin ? button(t('usage.downloadCsv'), async event => {
    const btn = event.currentTarget; btn.disabled = true; errors.replaceChildren();
    try {
      const response = await fetch(`/admin/v1/partners/${state.partnerId}/usage.csv?month=${encodeURIComponent(state.month)}`, { credentials: 'same-origin', headers: { 'Accept-Language': getLocale() } });
      if (!response.ok) { const problem = await response.json().catch(() => ({})); throw new ApiError(problem.detail || t('usage.csvError'), response.status, problem.requestId); }
      const url = URL.createObjectURL(await response.blob()); const link = el('a', { href: url, download: `consumo-${state.month}.csv` }); document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) { errors.replaceChildren(errorBox(e)); } finally { btn.disabled = false; }
  }, 'quiet') : null;
  main.append(el('div', { class: 'toolbar' }, el('div', { class: 'usage-period' }, month.wrapper, year.wrapper), csv), errors, host);
  let generation = 0;
  async function load() {
    const current = ++generation; host.replaceChildren(loading());
    try {
      const base = admin ? `/admin/v1/partners/${state.partnerId}/usage` : '/v1/usage';
      const usage = await request(`${base}?month=${encodeURIComponent(state.month)}`);
      if (revision !== state.revision || current !== generation) return;
      const issued = usage.licenses.reduce((sum, l) => sum + l.sessionsIssued, 0);
      host.replaceChildren(statsPanel(t('usage.figures', { index: padded(1) }), [stat(t('usage.authorised'), issued, t('usage.recordedHint')), stat(t('usage.started'), usage.startedSessions, t('usage.startedHint')), stat(t('usage.completed'), usage.completedSessions, t('usage.completedHint'))]), panel(t('usage.byLicense'), usage.licenses.length ? table([t('field.license'), t('usage.authorised'), t('license.monthlyQuota'), t('status.ready')], usage.licenses.map(l => [title(l.licenseName || l.name || t('usage.licenseFallback', { id: l.licenseId.slice(0, 8) }), l.licenseId), el('div', {}, number(l.sessionsIssued), l.unlimitedSessions ? null : el('progress', { class: 'meter', max: Math.max(1, l.monthlySessionQuota), value: l.sessionsIssued, 'aria-label': t('usage.quotaMeter') })), l.unlimitedSessions ? t('usage.noLimit') : number(l.monthlySessionQuota), l.unlimitedSessions ? t('usage.noLimit') : number(Math.max(0, l.monthlySessionQuota - l.sessionsIssued))])) : empty(t('usage.empty'))), el('div', { class: 'info' }, t('usage.explanation')));
    } catch (e) { if (revision === state.revision && current === generation) host.replaceChildren(errorBox(e), button(t('action.retry'), load, 'quiet')); }
  }
  function changePeriod() {
    if (!month.input.reportValidity() || !year.input.reportValidity()) return;
    const value = usagePeriodValue(year.input.value, month.input.value);
    if (!value || value === state.month) return;
    state.month = value; load();
  }
  month.input.addEventListener('change', changePeriod);
  year.input.addEventListener('change', changePeriod);
  load();
}
function settingsPage(main) {
  head(main, t('settings.title'), t('settings.description'));
  if (state.user.mustChangePassword) main.append(el('div', { class: 'info space-bottom', role: 'status' }, t('settings.mustChange')));
  const current = passwordField(t('settings.currentPassword'), 'currentPassword');
  const next = passwordField(t('settings.newPassword'), 'newPassword', 'new-password'); next.input.minLength = 12;
  const hint = el('small', {}, t('settings.passwordHint')); next.wrapper.append(hint);
  const confirm = passwordField(t('settings.confirmPassword'), 'confirmPassword', 'new-password');
  const errors = el('div'); const save = el('button', { type: 'submit', class: 'button primary' }, t('settings.save'));
  const form = el('form', { class: 'single-form' }, errors, current.wrapper, next.wrapper, confirm.wrapper, save);
  form.addEventListener('submit', async event => {
    event.preventDefault(); errors.replaceChildren();
    if (next.input.value !== confirm.input.value) { errors.replaceChildren(errorBox(new LocalizedError(t('settings.mismatch')))); confirm.input.focus(); return; }
    save.disabled = true;
    try { await request('/portal/api/password', { method: 'POST', body: { currentPassword: current.input.value, newPassword: next.input.value } }); current.input.value = ''; next.input.value = ''; confirm.input.value = ''; await session(); notify(t('settings.saved')); renderShell('settings'); } catch (e) { errors.replaceChildren(errorBox(e)); } finally { save.disabled = false; }
  });
  main.append(panel(t('settings.changeTitle'), el('div', { class: 'panel-body' }, form)));
}
window.addEventListener('pagehide', cleanupRoute);
window.addEventListener('hashchange', () => { if (state.user) renderRoute(location.hash.slice(1)); });
window.addEventListener('keydown', event => { if (event.key === 'Escape' && $('.shell.menu-open')) { $('.shell').classList.remove('menu-open'); $('.mobile-menu').setAttribute('aria-expanded', 'false'); $('.mobile-menu').focus(); } });
try { await session(); state.user ? renderShell() : renderLogin(); } catch (error) { app.replaceChildren(el('main', { id: 'main', class: 'boot' }, brand(), errorBox(error), button(t('action.retry'), () => location.reload(), 'primary'))); }

function languagePicker() {
  const selectId = id();
  const select = el('select', { id: selectId, 'aria-label': t('locale.label'), onchange: () => setLocale(select.value) },
    [['es', 'Español'], ['ca', 'Català'], ['en', 'English'], ['ar', 'العربية']].map(([value, label]) => el('option', { value, lang: value, dir: value === 'ar' ? 'rtl' : 'ltr' }, label)));
  select.value = getLocale();
  onLocaleChange(target => { target.value = getLocale(); }, select);
  return el('div', { class: 'locale-picker' }, el('label', { for: selectId }, t('locale.label')), select);
}
function updateValidation(input) {
  input.setCustomValidity('');
  const v = input.validity;
  if (v.valid) return;
  const key = v.valueMissing ? 'validation.required' : v.typeMismatch ? input.type === 'email' ? 'validation.email' : 'validation.url' : v.tooShort ? 'validation.length' : v.rangeUnderflow ? 'validation.minimum' : v.rangeOverflow ? 'validation.maximum' : v.patternMismatch ? 'validation.pattern' : 'validation.value';
  input.setCustomValidity(format(key, { min: input.type === 'number' ? number(Number(input.min)) : input.min || number(input.minLength), max: input.type === 'number' ? number(Number(input.max)) : input.max }));
}
document.addEventListener('invalid', event => { if (event.target.setCustomValidity) updateValidation(event.target); }, true);
document.addEventListener('input', event => { if (event.target.setCustomValidity) event.target.setCustomValidity(''); });
onLocaleChange(() => document.querySelectorAll('input, select').forEach(input => { if (input.validity.customError) updateValidation(input); }));
