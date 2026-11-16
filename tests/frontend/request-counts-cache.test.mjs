import assert from 'node:assert/strict';
import test from 'node:test';
import { RequestCountsCache } from '../../src/Frontend/src/app/requests/request-counts-cache.ts';

test('request counts expire and can be invalidated after changing filters or data', () => {
  const cache = new RequestCountsCache();
  const counts = { total: 5, newCount: 3, progressCount: 2 };
  assert.equal(cache.get(1000), null);
  cache.set(counts, 1000);
  assert.deepEqual(cache.get(30999), counts);
  assert.equal(cache.get(31000), null);
  cache.set(counts, 32000);
  cache.clear();
  assert.equal(cache.get(32001), null);
});
