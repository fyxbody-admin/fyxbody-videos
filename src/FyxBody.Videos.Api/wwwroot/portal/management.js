import { t, number, date, setText } from './i18n.js';

const active = license => license.status === 'active' && Date.parse(license.startsAt) <= Date.now() && Date.parse(license.endsAt) > Date.now();
const gb = bytes => number(bytes / 1_000_000_000);

export function managementViews(ui) {
  const { el, button, head, panel, table, empty, loading, errorBox, field, modal, request, all, paginated,
    badge, title, technical, notify, licenseForm, LocalizedError } = ui;
  const statusField = value => field(t('field.status'), 'status', { choices: ['active', 'suspended', 'revoked'], value: value || 'active' });
  const result = () => notify(t('management.saved'));

  async function partners(main, revision) {
    head(main, t('management.subpartners'), t('management.subpartnersDescription'));
    const settings = await request('/v1/management');
    if (!ui.isCurrent(revision)) return;
    if (!settings.canManageSubpartners) {
      main.append(panel(null, empty(t('management.noDelegation'))));
      return;
    }
    const root = el('div');
    main.append(root);
    let refresh;
    function list() {
      root.replaceChildren(panel(t('management.subpartners'), null, button(t('management.createChild'), () => edit(null, refresh), 'primary')));
      refresh = paginated(root, '/v1/subpartners', [t('field.name'), t('field.code'), t('field.status'), t('field.actions')],
        child => [title(child.name), technical(child.code), badge(child.status), el('div', { class: 'actions' },
          button(t('nav.licenses'), () => childLicenses(child), 'small quiet'),
          button(t('action.edit'), () => edit(child, refresh), 'small quiet'))], t('management.noChildren'));
    }
    function edit(child, reload) {
      const fields = [
        field(t('field.name'), 'name', { value: child?.name, maxlength: 200 }),
        field(t('field.code'), 'code', { value: child?.code, maxlength: 64, pattern: '[a-z0-9][a-z0-9_-]*' }),
        statusField(child?.status)
      ];
      modal(child ? t('management.editChild') : t('management.createChild'), fields.map(item => item.wrapper), t('license.save'), async form => {
        await request(child ? `/v1/subpartners/${child.id}` : '/v1/subpartners', { method: child ? 'PUT' : 'POST', body: ui.formValue(form) });
        result(); reload();
      });
    }
    function childLicenses(child) {
      const path = `/v1/subpartners/${child.id}/licenses`;
      const sourceLicenses = settings.licenses.filter(license => license.allowCatalogueDelegation && active(license));
      let reload;
      const openLicense = license => licenseForm(license, child.id, () => reload(), { path, allowManagement: false, allowUploads: settings.canConfigureSubpartnerUploads });
      root.replaceChildren(panel(child.name, null, el('div', { class: 'actions' }, button(t('management.backToChildren'), list, 'quiet'), button(t('license.createAction'), () => openLicense(null), 'primary'))));
      reload = paginated(root, path, [t('field.license'), t('field.status'), t('license.localValidity'), t('management.uploadQuota'), t('field.actions')],
        license => [title(license.name), badge(license.status), date(license.endsAt), quotaText(license),
          el('div', { class: 'actions' }, button(t('field.catalogue'), () => selectCatalogue(child, license, sourceLicenses), 'small quiet'),
            button(t('action.edit'), () => openLicense(license), 'small quiet'))], t('license.empty'));
    }
    list();
  }

  function quotaText(license) {
    if (!license.allowPartnerUploads) return t('status.disabled');
    if (license.uploadQuotaMode === 'storage') return t('management.gbValue', { count: gb(license.uploadQuotaLimit) });
    if (license.uploadQuotaMode === 'videos') return t('management.videoLimit', { count: number(license.uploadQuotaLimit) });
    return t('license.unlimited');
  }

  function selectCatalogue(child, license, sources) {
    if (!sources.length) {
      modal(t('field.catalogue'), empty(t('management.noSources')), null, null);
      return;
    }
    const source = field(t('management.sourceLicense'), 'sourceLicenseId', { choices: sources.map(item => [item.id, item.name]) });
    modal(t('field.catalogue'), source.wrapper, t('management.chooseSource'), async () => {
      const path = `/v1/subpartners/${child.id}/licenses/${license.id}/catalogue`;
      const sourceId = source.input.value;
      const route = ui.currentRevision();
      const [videos, assigned] = await Promise.all([
        all(`/v1/licenses/${sourceId}/delegable-videos`), request(`${path}?sourceLicenseId=${sourceId}`)
      ]);
      // Defer until the source chooser closes and restores focus.
      setTimeout(() => { if (ui.isCurrent(route)) editCatalogue(path, sourceId, videos, assigned.videoIds); }, 0);
    });
  }

  function editCatalogue(path, sourceId, videos, existing) {
    const selected = new Set(existing);
    const search = field(t('management.searchVideos'), 'search', { type: 'search', required: false });
    const allCurrent = el('input', { type: 'checkbox', name: 'allCurrent' });
    const listHost = el('div');
    const selectedCount = el('p', { class: 'help-line', role: 'status' });
    let page = 0;
    function draw() {
      const filtered = videos.filter(video => `${video.title} ${video.externalExerciseId}`.toLocaleLowerCase().includes(search.input.value.trim().toLocaleLowerCase()));
      const size = 25;
      const pages = Math.max(1, Math.ceil(filtered.length / size));
      page = Math.min(page, pages - 1);
      setText(selectedCount, t('management.selected', { count: number(allCurrent.checked ? videos.length : selected.size) }));
      search.input.disabled = allCurrent.checked;
      listHost.hidden = allCurrent.checked;
      if (allCurrent.checked) return;
      const rows = filtered.slice(page * size, (page + 1) * size).map(video => {
        const check = el('input', { type: 'checkbox', checked: selected.has(video.id), 'aria-label': video.title });
        check.addEventListener('change', () => {
          if (check.checked) selected.add(video.id); else selected.delete(video.id);
          setText(selectedCount, t('management.selected', { count: number(selected.size) }));
        });
        return [check, title(video.title), technical(video.externalExerciseId)];
      });
      listHost.replaceChildren(rows.length ? table([t('management.selectedColumn'), t('field.exercise'), t('field.sourceId')], rows, page * size) : empty(t('catalogue.partnerEmpty')),
        ui.pager({ page: page + 1, pageSize: size, total: filtered.length }, value => { page = value - 1; draw(); }));
    }
    search.input.addEventListener('input', () => { page = 0; draw(); });
    allCurrent.addEventListener('change', draw);
    draw();
    modal(t('field.catalogue'), [el('p', { class: 'help-line' }, t('management.snapshot')), el('label', { class: 'management-check' }, allCurrent, t('management.allCurrent')), selectedCount, search.wrapper, listHost],
      t('license.save'), async () => {
        if (!allCurrent.checked && selected.size > 1000) throw new LocalizedError(t('management.selectionLimit'));
        await request(path, { method: 'PUT', body: { sourceLicenseId: sourceId, allCurrent: allCurrent.checked, videoIds: allCurrent.checked ? null : [...selected] } });
        result();
      }, { wide: true });
  }

  async function uploads(main, revision) {
    head(main, t('management.privateVideos'), t('management.privateDescription'));
    const settings = await request('/v1/management');
    if (!ui.isCurrent(revision)) return;
    const licenses = settings.licenses;
    if (!licenses.length) {
      main.append(panel(null, empty(t('license.empty'))));
      return;
    }
    const selector = field(t('field.license'), 'licenseId', { choices: licenses.map(item => [item.id, item.name]),
      value: licenses.find(license => license.allowPartnerUploads && active(license))?.id });
    const host = el('div');
    main.append(panel(null, selector.wrapper), host);
    let renderCount = 0;
    async function load() {
      const serial = ++renderCount;
      const license = licenses.find(item => item.id === selector.input.value);
      const base = `/v1/licenses/${license.id}`;
      host.replaceChildren(loading());
      try {
        const usage = await request(`${base}/private-video-usage`);
        if (!ui.isCurrent(revision) || serial !== renderCount) return;
        const summary = t('management.usage', { bytes: gb(usage.storageBytes), count: number(usage.videoCount) });
        const create = button(t('management.upload'), () => uploadVideo(base, load), 'primary', { disabled: !usage.allowPartnerUploads || !usage.licenseActive });
        host.replaceChildren(panel(t('management.uploadQuota'), el('div', {}, el('p', {}, quotaText({ ...license, uploadQuotaMode: usage.quotaMode, uploadQuotaLimit: usage.quotaLimit, allowPartnerUploads: usage.allowPartnerUploads })), el('p', {}, summary)), create));
        paginated(host, `${base}/private-videos`, [t('field.exercise'), t('management.partnerReference'), t('field.actions')],
          video => [title(video.title), video.partnerReference || '—', el('div', { class: 'actions' },
            video.status === 'archived' ? badge(video.status) : button(t('action.edit'), () => editVideo(video, load), 'small quiet'),
            button(t('management.delete'), () => deleteVideo(video, load), 'small quiet'))], t('management.noPrivate'));
      } catch (error) {
        if (ui.isCurrent(revision) && serial === renderCount) host.replaceChildren(errorBox(error));
      }
    }
    selector.input.addEventListener('change', load);
    await load();
  }

  function editVideo(video, reload) {
    const name = field(t('field.title'), 'title', { maxlength: 300, value: video.title });
    const reference = field(t('management.partnerReference'), 'partnerReference', { required: false, maxlength: 128, value: video.partnerReference });
    modal(t('action.edit'), [name.wrapper, reference.wrapper], t('license.save'), async form => {
      await request(`/v1/private-videos/${video.id}`, { method: 'PATCH', body: ui.formValue(form) });
      result(); reload();
    });
  }

  function deleteVideo(video, reload) {
    modal(t('management.delete'), el('p', {}, t('management.deleteWarning', { title: video.title })), t('management.delete'), async () => {
      await request(`/v1/private-videos/${video.id}`, { method: 'DELETE' });
      result(); reload();
    });
  }

  function uploadVideo(base, reload) {
    const name = field(t('field.title'), 'title', { maxlength: 300 });
    const reference = field(t('management.partnerReference'), 'partnerReference', { required: false, maxlength: 128 });
    const file = field(t('upload.fileLabel'), 'file', { type: 'file', hint: t('upload.fileHint') });
    file.input.accept = 'video/mp4,.mp4';
    const language = field(t('field.language'), 'language', { value: 'es', maxlength: 35, pattern: '[a-z]{2,3}(-[A-Za-z0-9]{2,8})*' });
    const rendition = field(t('field.rendition'), 'rendition', { value: 'original', maxlength: 50, pattern: '[A-Za-z0-9_-]+' });
    let key = crypto.randomUUID();
    const view = modal(t('management.upload'), [name.wrapper, reference.wrapper, file.wrapper, language.wrapper, rendition.wrapper],
      t('upload.submit'), async form => {
        const data = new FormData(form);
        const selected = data.get('file');
        const uploadKey = key;
        if (!selected?.size || selected.size > 200 * 1024 * 1024) throw new LocalizedError(t('upload.fileHint'));
        data.set('durationMs', String(await duration(selected)));
        await request(`${base}/private-videos`, { method: 'POST', body: data, headers: { 'Idempotency-Key': uploadKey } });
        result(); reload();
      });
    view.form.addEventListener('input', () => { key = crypto.randomUUID(); });
  }

  function duration(file) {
    return new Promise((resolve, reject) => {
      const media = document.createElement('video');
      const url = URL.createObjectURL(file);
      const timer = setTimeout(() => done(0), 10000);
      function done(value) {
        clearTimeout(timer);
        media.onloadedmetadata = media.onerror = null;
        media.removeAttribute('src');
        media.load();
        URL.revokeObjectURL(url);
        if (Number.isFinite(value) && value > 0) resolve(value);
        else reject(new LocalizedError(t('upload.waitMetadata')));
      }
      media.preload = 'metadata';
      media.onloadedmetadata = () => done(Math.round(media.duration * 1000));
      media.onerror = () => done(0);
      media.src = url;
    });
  }

  return { partners, uploads };
}
