import assert from 'node:assert/strict';
import test from 'node:test';
import { changeResidentCounts } from '../../src/Frontend/src/app/residents/resident-counts.ts';

test('updates counts after a decision or resident type change', () => {
  const counts = { total: 5, owners: 2, familyMembers: 1, tenants: 1, employees: 1 };
  assert.deepEqual(changeResidentCounts(counts, 'Owner', null),
    { total: 4, owners: 1, familyMembers: 1, tenants: 1, employees: 1 });
  assert.deepEqual(changeResidentCounts(counts, 'Tenant', 'Employee'),
    { total: 5, owners: 2, familyMembers: 1, tenants: 0, employees: 2 });
  assert.equal(changeResidentCounts(null, 'Owner', null), null);
  assert.equal(counts.total, 5);
});
