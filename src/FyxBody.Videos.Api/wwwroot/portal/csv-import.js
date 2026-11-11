import { t, number, setText } from './i18n.js';
import { buildCsvTemplate, csvTemplateFilename, CsvInputError, canQueueCsv, createCsvImportSession } from './csv-import-model.js';
export function downloadCsvTemplate() {
  const url = URL.createObjectURL(new Blob([buildCsvTemplate()], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url; link.download = csvTemplateFilename;
  document.body.append(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export function openCsvImport({ el, button, field, modal, technical, table, errorBox, request, alive, onQueued, signal }) {
  if (!alive()) return;
  const file = field(t('csv.fileLabel'), 'csvFile', { type: 'file', required: false, hint: t('csv.fileHint') });
  file.input.accept = '.csv,text/csv'; file.input.classList.add('sr-only');
  const filename = el('span', { class: 'file-name', dir: 'auto', role: 'status' }, t('upload.noFile'));
  const choose = button(t('csv.chooseFile'), () => file.input.click(), 'quiet');
  file.wrapper.append(el('div', { class: 'file-chooser' }, choose, filename));
  const errorHost = el('div');
  const status = el('p', { class: 'help-line', role: 'status', 'aria-live': 'polite' });
  const reportHost = el('div', { class: 'csv-report' });
  const preview = button(t('csv.preview'), () => { if (active()) session.preview(); }, 'primary', { disabled: true });
  const queue = button(t('csv.confirm'), () => { if (active()) session.queue(); }, 'primary', { disabled: true, hidden: true });
  const content = el('div', { class: 'csv-import' },
    el('p', {}, t('csv.intro')),
    el('div', { class: 'info' }, el('p', {}, t('csv.columns')), el('p', { class: 'help-line' }, t('csv.qualityHint'))),
    el('p', { class: 'help-line' }, t('csv.templateHint')), file.wrapper,
    errorHost, status, reportHost,
    el('p', { class: 'help-line' }, t('csv.confirmHint')),
    el('div', { class: 'actions csv-actions' }, preview, queue));
  const view = modal(t('csv.title'), content, null, null, { wide: true });
  const session = createCsvImportSession({ request, onQueued, onChange: render });
  const active = () => alive() && view.dialog.isConnected && view.dialog.open;
  file.input.addEventListener('change', () => { if (active()) session.selectFile(file.input.files?.[0]); });
  const closeForRoute = () => view.close();
  signal?.addEventListener('abort', closeForRoute, { once: true });
  view.dialog.addEventListener('close', () => { session.dispose(); file.input.value = ''; signal?.removeEventListener('abort', closeForRoute); });
  render(session.state);
  function render(snapshot) {
    if (!active()) return;
    const pending = ['reading', 'previewing', 'queuing'].includes(snapshot.phase);
    choose.disabled = pending || snapshot.phase === 'queued';
    file.input.disabled = choose.disabled;
    setText(filename, snapshot.filename || t('upload.noFile'));
    preview.disabled = pending || !snapshot.csv || snapshot.phase === 'queued';
    queue.hidden = !snapshot.report || snapshot.phase === 'queued';
    queue.disabled = pending || snapshot.phase !== 'previewed' || !canQueueCsv(snapshot.report);
    preview.classList.toggle('primary', queue.hidden || queue.disabled);
    queue.classList.toggle('primary', !queue.disabled);
    setText(preview, snapshot.phase === 'previewing' ? t('csv.previewing') : t('csv.preview'));
    setText(queue, snapshot.phase === 'queuing' ? t('csv.queuing') : t('csv.confirm'));
    setText(status, pending ? t(`csv.${snapshot.phase}`) : snapshot.phase === 'queued' ? t('csv.queued') : snapshot.phase === 'selected' ? t('csv.selected') : '');
    errorHost.replaceChildren();
    if (snapshot.error) {
      errorHost.append(snapshot.error instanceof CsvInputError ? el('div', { class: 'error', role: 'alert' }, t(`csv.${snapshot.error.code}`)) : errorBox(snapshot.error));
      if (snapshot.uncertain) errorHost.append(el('p', { class: 'help-line' }, t('csv.uncertain')));
    }
    reportHost.replaceChildren();
    if (!snapshot.report) return;
    const report = snapshot.report;
    reportHost.append(el('p', { class: 'csv-summary', role: 'status' }, t('csv.summary', { total: number(report.totalRows), valid: number(report.validRows), invalid: number(report.invalidRows) })));
    if (report.errors?.length) reportHost.append(el('div', { class: 'error', role: 'alert' }, el('ul', {}, report.errors.map(error => el('li', {}, error.message)))));
    if (!report.queued) reportHost.append(el('p', { class: canQueueCsv(report) ? 'help-line' : 'import-error' }, canQueueCsv(report) ? t('csv.ready') : t('csv.fixErrors')));
    if (report.rows?.length) reportHost.append(table([t('csv.line'), t('csv.reference'), t('field.title'), t('csv.url'), t('csv.qualities'), t('csv.result')], report.rows.map(row => {
      const result = row.errors?.length ? el('ul', { class: 'csv-row-errors' }, row.errors.map(error => el('li', {}, error.message))) : el('div', {},
        el('span', { class: `badge ${report.queued ? 'pending' : 'complete'}` }, row.reused ? t('csv.reused') : report.queued ? t('import.status.queued') : t('csv.valid')),
        row.jobId ? el('small', { class: 'csv-job-id' }, technical(row.jobId)) : null);
      return [technical(row.rowNumber), technical(row.effectiveReference || row.reference || '—'),
        row.title || el('small', {}, t('csv.sourceTitle')), technical(row.url || '—'),
        technical(row.heights?.map(height => `${height}p`).join('; ') || '—'), result];
    })));
  }
}
