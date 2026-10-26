import assert from 'node:assert/strict';
import test from 'node:test';
import { ResidentDirectory } from '../../src/Frontend/src/app/residents/resident-directory.ts';

const resident = (id, overrides = {}) => ({
  id: String(id),
  telegramChatId: String(id),
  firstName: 'Олена',
  lastName: 'Ковальчук',
  phone: null,
  buildingNumber: '4Б',
  registeredAt: new Date(Date.UTC(2026, 0, id)).toISOString(),
  ...overrides,
});

test('sorts the entire directory before splitting it into pages of at most 25', () => {
  const directory = new ResidentDirectory();
  directory.setItems(Array.from({ length: 61 }, (_, i) => resident(i + 1)));
  assert.equal(directory.pageCount, 3);
  assert.deepEqual([0, 1, 2].map(i => directory.page(i).length), [25, 25, 11]);
  const all = [0, 1, 2].flatMap(i => directory.page(i));
  assert.deepEqual(all.map(r => r.id), Array.from({ length: 61 }, (_, i) => String(61 - i)));
  assert.equal(new Set(all.map(r => r.id)).size, 61);
  directory.applyFilters({ sort: 'registeredAt', direction: 'asc', building: '' });
  assert.equal(directory.page(0)[0].id, '1');
  assert.equal(directory.page(2).at(-1).id, '61');
});

test('uses Ukrainian surname order in both directions and keeps unnamed profiles last', () => {
  const lastNames = ['Йосипенко', 'Їжак', 'Іваненко', 'Ємець', 'Дмитрук', 'Ґалаган', 'Гнатюк'];
  const directory = new ResidentDirectory();
  directory.setItems([
    resident(1, { lastName: null }),
    ...lastNames.map((lastName, i) => resident(i + 2, { lastName })),
  ]);
  directory.applyFilters({ sort: 'lastName', direction: 'asc', building: '' });
  assert.deepEqual(directory.page(0).map(r => r.lastName), [...lastNames].reverse().concat(null));
  directory.applyFilters({ ...directory.filters, direction: 'desc' });
  assert.deepEqual(directory.page(0).map(r => r.lastName), lastNames.concat(null));
});

test('compares registration instants, with deterministic IDs for tied dates or names', () => {
  const directory = new ResidentDirectory();
  directory.setItems([
    resident(3, { registeredAt: '2026-01-01T12:00:00+02:00' }),
    resident(2, { registeredAt: '2026-01-01T11:00:00Z' }),
    resident(1, { registeredAt: '2026-01-01T11:00:00Z' }),
  ]);
  assert.deepEqual(directory.page(0).map(r => r.id), ['1', '2', '3']);
  directory.applyFilters({ sort: 'lastName', direction: 'asc', building: '' });
  assert.deepEqual(directory.page(0).map(r => r.id), ['1', '2', '3']);
});

test('filters all four buildings and historical profiles before pagination', () => {
  const directory = new ResidentDirectory();
  const buildings = ['4Б', '4В', '4Г', '4Д', null];
  directory.setItems(Array.from({ length: 150 }, (_, i) => resident(i + 1, {
    buildingNumber: buildings[i % 5],
  })));
  for (const building of [...buildings.slice(0, 4), 'Unspecified']) {
    directory.applyFilters({ sort: 'registeredAt', direction: 'desc', building });
    assert.equal(directory.pageCount, 2);
    assert.deepEqual([directory.page(0).length, directory.page(1).length], [25, 5]);
    assert.ok([...directory.page(0), ...directory.page(1)].every(r =>
      r.buildingNumber === (building === 'Unspecified' ? null : building)));
  }
  directory.applyFilters({ ...directory.filters, building: '' });
  assert.equal(directory.pageCount, 6);
});

test('refresh replaces stale profiles, retains filters and removes repeated users across API pages', () => {
  const directory = new ResidentDirectory();
  directory.applyFilters({ sort: 'lastName', direction: 'asc', building: '4В' });
  directory.setItems([
    resident(1),
    resident(2, { buildingNumber: '4В' }),
    resident(3, { telegramChatId: '2', buildingNumber: '4В', lastName: 'Новий' }),
  ]);
  assert.deepEqual(directory.page(0).map(r => r.id), ['3']);
  directory.setItems([]);
  assert.equal(directory.pageCount, 1);
  assert.deepEqual(directory.page(0), []);
  assert.equal(directory.filters.building, '4В');
});
