import assert from 'node:assert/strict';
import test from 'node:test';
import { PendingDirectory } from '../../src/Frontend/src/app/residents/pending-directory.ts';

test('filters all registrations before pagination and retains missing addresses', () => {
  const directory = new PendingDirectory();
  directory.setItems(Array.from({ length: 90 }, (_, i) => ({
    id: String(i), buildingNumber: i < 35 ? '4Б' : i < 70 ? '4В' : null,
  })));
  directory.building = '4Б';
  assert.equal(directory.pageCount, 2);
  assert.deepEqual([directory.page(0).length, directory.page(1).length], [25, 10]);
  assert.equal(directory.all().length, 35);
  directory.remove('0');
  assert.equal(directory.all().length, 34);
  directory.building = 'Unspecified';
  assert.equal(directory.all().length, 20);
  directory.building = '';
  assert.equal(directory.all().length, 89);
});
