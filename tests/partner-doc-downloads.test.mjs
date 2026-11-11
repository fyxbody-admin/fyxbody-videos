import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const root = new URL('../', import.meta.url);
const read = path => readFile(new URL(path, root), 'utf8');

test('downloaded backend client stays identical to the maintained server-only example', async () => {
  assert.equal(await read('src/FyxBody.Videos.Api/wwwroot/portal/integration/backend-client.mjs.txt'), await read('examples/backend-client.mjs'));
});

test('downloaded OpenAPI matches the canonical contract with a portable origin and only partner operations', async () => {
  const actual = JSON.parse(await read('src/FyxBody.Videos.Api/wwwroot/portal/integration/partner-openapi.json'));
  const expected = JSON.parse(await read('docs/openapi/partner.json'));
  expected.servers = [{ url: '/' }];
  assert.deepEqual(actual, expected);
  assert.ok(Object.keys(actual.paths).every(path => path.startsWith('/v1/')));
  assert.ok(actual.paths['/v1/videos'].get.parameters.some(parameter => parameter.name === 'externalExerciseId'));
  assert.ok(actual.paths['/v1/subpartners/{subpartnerId}/licenses/{licenseId}/catalogue'].put);
  const upload = actual.paths['/v1/licenses/{licenseId}/private-videos'].post;
  assert.ok(upload.parameters.some(parameter => parameter.name === 'Idempotency-Key' && parameter.required));
  assert.equal(upload.requestBody.content['multipart/form-data'].schema.properties.file.format, 'binary');
  for (const item of Object.values(actual.paths)) for (const operation of Object.values(item)) {
    assert.ok(operation.security.some(scheme => 'PartnerKey' in scheme));
    assert.ok(!operation.security.some(scheme => 'AdminKey' in scheme));
  }
  assert.equal(actual.components.schemas.LicenseUsageResponse.properties.unlimitedSessions.type, 'boolean');
});
