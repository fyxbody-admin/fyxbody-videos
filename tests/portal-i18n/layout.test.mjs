import test from 'node:test';
import assert from 'node:assert/strict';
import { observeHeaderHeight } from '../../src/FyxBody.Videos.Api/wwwroot/portal/layout.js';

test('mobile menu tracks wrapped/localized header height and disconnects on shell removal', () => {
  let height = 84, resize, observed, disconnected = false;
  const properties = new Map();
  const header = { getBoundingClientRect: () => ({ height }) };
  const shell = { querySelector: selector => selector === '.topbar' ? header : null, style: { setProperty: (name, value) => properties.set(name, value) } };
  class Observer {
    constructor(callback) { resize = callback; }
    observe(node) { observed = node; }
    disconnect() { disconnected = true; }
  }
  const cleanup = observeHeaderHeight(shell, Observer);
  assert.equal(observed, header);
  assert.equal(properties.get('--topbar-height'), '84px');
  height = 122.17; resize();
  assert.equal(properties.get('--topbar-height'), '123px');
  height = 92.5; resize();
  assert.equal(properties.get('--topbar-height'), '93px');
  cleanup();
  assert.equal(disconnected, true);
});
