import assert from 'node:assert/strict';
import test from 'node:test';
import { PendingCountCache } from '../../src/Frontend/src/app/residents/pending-count-cache.ts';

test('pending count including zero is cached for 30 seconds and can be invalidated', () => {
  const cache = new PendingCountCache();
  assert.equal(cache.get(1000), null);
  cache.set(0, 1000);
  assert.equal(cache.get(30999), 0);
  assert.equal(cache.get(31000), null);
  cache.set(5, 32000);
  cache.clear();
  assert.equal(cache.get(32001), null);
});
