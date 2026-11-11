import test from 'node:test';
import assert from 'node:assert/strict';
import { buildCsvTemplate, csvTemplateFilename, csvMaxBytes, decodeCsvFile, canQueueCsv, createCsvImportSession } from '../../src/FyxBody.Videos.Api/wwwroot/portal/csv-import-model.js';
const encode = text => new TextEncoder().encode(text);
const csvA = 'Referencia,Titulo,URL,Calidades\r\n,Árabe تمرين,https://youtu.be/AAAAAAAAAAA,1080p;720p\r\n';
const csvB = 'Referencia,Titulo,URL,Calidades\nB,,https://youtu.be/BBBBBBBBBBB,480p\n';
const file = (csv = csvA, name = 'ejercicios.csv') => ({ name, size: encode(csv).byteLength, async arrayBuffer() { return encode(csv).buffer; } });
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const report = (update = {}) => ({ queued: false, canImport: true, totalRows: 1, validRows: 1, invalidRows: 0, errors: [], rows: [{ rowNumber: 2, reference: null, title: 'Árabe تمرين', url: 'https://www.youtube.com/watch?v=AAAAAAAAAAA', heights: [1080, 720], effectiveReference: 'youtube:AAAAAAAAAAA', errors: [], jobId: null, reused: false }], ...update });

test('canonical download is BOM UTF-8, CRLF, fixed headers, optional fields and inert replacement URLs', () => {
  const template = buildCsvTemplate();
  assert.equal(csvTemplateFilename, 'fyxbody-youtube-import-template.csv');
  assert.deepEqual([...encode(template).slice(0, 3)], [239, 187, 191]);
  const [header, first, second, empty] = template.slice(1).split('\r\n');
  assert.equal(header, 'Referencia,Titulo,URL,Calidades');
  assert.equal(first.split(',').length, 4);
  assert.equal(second.split(',').length, 4);
  assert.ok(first.startsWith(',,'));
  assert.match(first, /,1080p;720p;480p$/);
  assert.match(second, /^EXAMPLE-REF-/);
  assert.match(second, /,720p;480p;360p$/);
  assert.equal(empty, '');
  assert.equal((template.match(/REPLACE_WITH_ID/g) || []).length, 2);
  assert.doesNotMatch(template, /znWpziD4Vvs|mDa8SFe0zLI|WhpknPP0xKU/);
});

test('UTF-8 decoder preserves Unicode and quotes, accepts exact size and rejects malformed, empty and oversize bytes', () => {
  assert.equal(decodeCsvFile(encode('\uFEFF' + csvA)), csvA);
  const quoted = 'Referencia,Titulo,URL,Calidades\nA,"Título con , y ""comillas""",https://youtu.be/AAAAAAAAAAA,720p';
  assert.equal(decodeCsvFile(encode(quoted)), quoted);
  assert.equal(decodeCsvFile(encode('x'.repeat(csvMaxBytes))).length, csvMaxBytes);
  for (const [bytes, code] of [[new Uint8Array([0xc3, 0x28]), 'encoding'], [encode('\uFEFF \r\n'), 'empty'], [new Uint8Array(csvMaxBytes + 1), 'tooLarge']]) assert.throws(() => decodeCsvFile(bytes), error => error.code === code);
});

test('selecting a file never calls the API and size is checked before reading', async () => {
  let reads = 0;
  const session = createCsvImportSession({ request() { assert.fail('selection must not request'); } });
  await session.selectFile(file());
  assert.equal(session.state.phase, 'selected');
  assert.equal(session.state.csv, csvA);
  await session.selectFile({ name: 'large.csv', size: csvMaxBytes + 1, arrayBuffer() { reads++; } });
  assert.equal(reads, 0);
  assert.equal(session.state.error.code, 'tooLarge');
  assert.equal(session.state.csv, '');
});

test('preview and explicit confirmation send the same snapshot once, success exposes job IDs and refreshes history', async () => {
  const calls = [], pending = deferred(), queued = [];
  const session = createCsvImportSession({ request(path, options) { calls.push({ path, options }); return calls.length === 1 ? report() : pending.promise; }, onQueued(value) { queued.push(value); } });
  await session.selectFile(file());
  await session.queue();
  assert.equal(calls.length, 0);
  await session.preview();
  assert.equal(calls.length, 1);
  assert.equal(session.state.phase, 'previewed');
  assert.ok(canQueueCsv(session.state.report));
  const posting = session.queue();
  await session.queue();
  await session.preview();
  assert.equal(calls.length, 2);
  assert.equal(calls[0].path, '/admin/v1/video-imports/csv/preview');
  assert.equal(calls[1].path, '/admin/v1/video-imports/csv');
  for (const call of calls) assert.deepEqual(call.options, { method: 'POST', body: { csv: csvA } });
  const accepted = report({ queued: true, rows: [{ ...report().rows[0], jobId: '11111111-1111-4111-8111-111111111111', reused: true }] });
  pending.resolve(accepted); await posting;
  assert.equal(session.state.phase, 'queued');
  assert.equal(session.state.report.rows[0].jobId, accepted.rows[0].jobId);
  assert.equal(session.state.report.rows[0].reused, true);
  assert.deepEqual(queued, [accepted]);
  await session.queue(); assert.equal(calls.length, 2);
});

test('any global error, row error or inconsistent count prevents enqueue of the entire batch', async () => {
  const failures = [
    report({ canImport: false }), report({ invalidRows: 1 }), report({ totalRows: 0, validRows: 0, rows: [] }),
    report({ errors: [{ code: 'csv_syntax', message: 'Bad CSV' }] }),
    report({ rows: [{ ...report().rows[0], errors: [{ code: 'csv_url', message: 'Bad URL' }] }] }),
    report({ totalRows: 2 }), report({ validRows: 0 }), report({ totalRows: 501 })
  ];
  for (const invalid of failures) {
    let calls = 0;
    const session = createCsvImportSession({ request() { calls++; return invalid; } });
    await session.selectFile(file()); await session.preview(); await session.queue();
    assert.equal(calls, 1); assert.equal(canQueueCsv(invalid), false);
  }
});

test('changing a file clears the report immediately and late reads/previews cannot restore the old file', async () => {
  const pending = deferred(), reading = deferred();
  const session = createCsvImportSession({ request() { return pending.promise; } });
  await session.selectFile(file());
  const preview = session.preview();
  const slowRead = session.selectFile({ name: 'slow.csv', size: 10, arrayBuffer() { return reading.promise; } });
  await session.selectFile(file(csvB, 'new.csv'));
  pending.resolve(report()); await preview;
  reading.resolve(encode(csvA).buffer); await slowRead;
  assert.equal(session.state.filename, 'new.csv');
  assert.equal(session.state.csv, csvB);
  assert.equal(session.state.report, null);
  assert.equal(session.state.phase, 'selected');
});

test('a lost enqueue response requires fresh preview before retry; server rejection remains visible', async () => {
  const calls = [];
  const session = createCsvImportSession({ request(path) { calls.push(path); if (calls.length === 2) throw new TypeError('network'); return report(); } });
  await session.selectFile(file()); await session.preview(); await session.queue();
  assert.equal(session.state.phase, 'selected');
  assert.equal(session.state.report, null);
  assert.equal(session.state.uncertain, true);
  await session.queue(); assert.equal(calls.length, 2);
  await session.preview(); assert.equal(session.state.uncertain, false);
  const rejected = report({ canImport: false, invalidRows: 1 });
  const other = createCsvImportSession({ request(path) { return path.endsWith('/preview') ? report() : rejected; } });
  await other.selectFile(file()); await other.preview(); await other.queue();
  assert.equal(other.state.phase, 'previewed');
  assert.equal(other.state.report, rejected);
  assert.equal(canQueueCsv(other.state.report), false);
});

test('closing removes retained file and ignores late UI updates, while an accepted queue still refreshes history', async () => {
  const pending = deferred(), updates = [], queued = [];
  const session = createCsvImportSession({ request(path) { return path.endsWith('/preview') ? report() : pending.promise; }, onChange(value) { updates.push(value.phase); }, onQueued(value) { queued.push(value); } });
  await session.selectFile(file()); await session.preview();
  const posting = session.queue(); session.dispose();
  const count = updates.length;
  pending.resolve(report({ queued: true })); await posting;
  assert.equal(updates.length, count);
  assert.equal(session.state.csv, '');
  assert.equal(session.state.report, null);
  assert.equal(queued.length, 1);
});
