import assert from 'node:assert/strict';
import test from 'node:test';
import { countRequests } from '../../src/Frontend/src/app/requests/request-counts.ts';

test('counts all matching pages, including empty pages, for either section and filters', async () => {
  for (const section of ['complex', 'keys']) {
    const filters = { section, status: ['New', 'InProgress'], target: 'Building4Б' };
    const pages = {
      start: { items: [{ request: { status: 'New' } }], cursor: 'next' },
      next: { items: [], cursor: 'last' },
      last: { items: [{ request: { status: 'New' } }, { request: { status: 'InProgress' } }], cursor: null },
    };
    const seen = [];
    const fetch = async (actual, cursor) => { seen.push([actual, cursor]); return pages[cursor ?? 'start']; };
    assert.deepEqual(await countRequests(filters, fetch, () => true, pages.start), { total: 3, newCount: 2, progressCount: 1 });
    assert.deepEqual(seen, [[filters, 'next'], [filters, 'last']]);
  }
});

test('does not return partial counts for stale sessions or repeated cursors', async () => {
  assert.equal(await countRequests({}, async () => ({ items: [], cursor: 'next' }), () => false), null);
  await assert.rejects(countRequests({}, async () => ({ items: [], cursor: 'same' }), () => true), /Repeated request cursor/);
});
