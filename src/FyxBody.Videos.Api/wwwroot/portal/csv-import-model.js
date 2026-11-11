// A selected CSV is held only for this dialog. Previewing never queues jobs.
export const csvMaxBytes = 1024 * 1024;
export const csvMaxRows = 500;
export const csvTemplateFilename = 'fyxbody-youtube-import-template.csv';
export function buildCsvTemplate() {
  return '\uFEFFReferencia,Titulo,URL,Calidades\r\n,,https://www.youtube.com/watch?v=REPLACE_WITH_ID,1080p;720p;480p\r\nEXAMPLE-REF-002,Mi ejercicio,https://www.youtube.com/watch?v=REPLACE_WITH_ID,720p;480p;360p\r\n';
}
export class CsvInputError extends Error {
  constructor(code) { super(code); this.code = code; }
}
export function decodeCsvFile(bytes) {
  if (bytes.byteLength > csvMaxBytes) throw new CsvInputError('tooLarge');
  let csv;
  try { csv = new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
  catch { throw new CsvInputError('encoding'); }
  if (!csv.trim()) throw new CsvInputError('empty');
  return csv;
}
export function canQueueCsv(report) {
  return report?.canImport === true && report.totalRows > 0 && report.totalRows <= csvMaxRows && report.validRows === report.totalRows && report.invalidRows === 0 &&
    !report.errors?.length && Array.isArray(report.rows) && report.rows.length === report.totalRows &&
    report.rows.every(row => !row.errors?.length);
}
export function createCsvImportSession({ request, onChange = () => {}, onQueued = () => {} }) {
  let current = { phase: 'empty', filename: '', csv: '', report: null, error: null };
  let revision = 0, disposed = false;
  const publish = update => { if (!disposed) { current = { ...current, ...update }; onChange(current); } };
  const stillCurrent = token => !disposed && token === revision;
  return {
    get state() { return current; },
    async selectFile(file) {
      if (disposed) return;
      const token = ++revision;
      publish({ phase: file ? 'reading' : 'empty', filename: file?.name || '', csv: '', report: null, error: null, uncertain: false });
      if (!file) return;
      try {
        if (file.size > csvMaxBytes) throw new CsvInputError('tooLarge');
        const csv = decodeCsvFile(await file.arrayBuffer());
        if (stillCurrent(token)) publish({ phase: 'selected', csv });
      } catch (error) { if (stillCurrent(token)) publish({ phase: 'empty', error }); }
    },
    async preview() {
      if (disposed || !['selected', 'previewed'].includes(current.phase) || !current.csv) return;
      const token = ++revision, csv = current.csv;
      publish({ phase: 'previewing', report: null, error: null, uncertain: false });
      try {
        const report = await request('/admin/v1/video-imports/csv/preview', { method: 'POST', body: { csv } });
        if (stillCurrent(token)) publish({ phase: 'previewed', report });
      } catch (error) { if (stillCurrent(token)) publish({ phase: 'selected', error }); }
    },
    async queue() {
      if (disposed || current.phase !== 'previewed' || !canQueueCsv(current.report)) return;
      const token = ++revision, csv = current.csv;
      publish({ phase: 'queuing', error: null });
      try {
        const report = await request('/admin/v1/video-imports/csv', { method: 'POST', body: { csv } });
        // A successful enqueue refreshes history even when its dialog has closed.
        if (report.queued) onQueued(report);
        if (stillCurrent(token)) publish({ phase: report.queued ? 'queued' : 'previewed', report });
      } catch (error) {
        // A lost response can follow an accepted write: require a new preview before another submission.
        if (stillCurrent(token)) publish({ phase: 'selected', report: null, error, uncertain: true });
      }
    },
    dispose() { disposed = true; revision++; current = { phase: 'empty', filename: '', csv: '', report: null, error: null }; }
  };
}
