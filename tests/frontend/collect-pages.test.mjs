import assert from 'node:assert/strict';
import test from 'node:test';
import { collectPages } from '../../src/Frontend/src/app/export/collect-pages.ts';
import { ResidentDirectory } from '../../src/Frontend/src/app/residents/resident-directory.ts';

test('collects every page, including empty filtered pages', async () => {
  const calls = [];
  const pages = { start: { items: [1, 2], cursor: 'next' }, next: { items: [], cursor: 'last' }, last: { items: [3], cursor: null } };
  const result = await collectPages(async cursor => { calls.push(cursor); return pages[cursor ?? 'start']; }, () => true);
  assert.deepEqual(result, [1, 2, 3]);
  assert.deepEqual(calls, [null, 'next', 'last']);
});

test('stops on stale session and rejects repeated cursors', async () => {
  let calls = 0;
  assert.equal(await collectPages(async () => { calls++; return { items: [1], cursor: 'next' }; }, () => false), null);
  assert.equal(calls, 1);
  await assert.rejects(collectPages(async () => ({ items: [], cursor: 'repeat' }), () => true), /Repeated export cursor/);
});

test('exports the full filtered and sorted resident directory, not just one page', () => {
  const directory = new ResidentDirectory();
  directory.setItems(Array.from({length: 80}, (_, i) => ({ id: String(i), telegramChatId: String(i), firstName: 'Ім’я', lastName: `Прізвище ${String(i).padStart(2, '0')}`, buildingNumber: i % 2 ? '4Б' : '4В', registeredAt: '2026-01-01T00:00:00Z' })));
  directory.applyFilters({ sort: 'lastName', direction: 'asc', building: '4Б' });
  assert.equal(directory.page(0).length, 25);
  assert.equal(directory.all().length, 40);
  assert.ok(directory.all().every(r => r.buildingNumber === '4Б'));
  assert.equal(directory.all()[0].id, '1');
  assert.equal(directory.all().at(-1).id, '79');
});
