import assert from 'node:assert/strict';
import test from 'node:test';
import { aggregateStatistics, sortStatistics } from '../../src/Frontend/src/app/statistics/statistics-sort.ts';

const entries = [
  { date: '2026-01-01', endpoint: 'a', requests: 2, readUnits: 5, writeUnits: 1, durationMs: 40 },
  { date: '2026-01-02', endpoint: 'b', requests: 4, readUnits: 2, writeUnits: 7, durationMs: 20 },
  { date: '2026-01-03', endpoint: 'c', requests: 1, readUnits: 3, writeUnits: 2, durationMs: 10 },
];

test('sorts numeric metrics and averages in both directions without mutating the source', () => {
  for (const [column, expected] of [
    ['requests', ['c', 'a', 'b']],
    ['readUnits', ['b', 'c', 'a']],
    ['writeUnits', ['a', 'c', 'b']],
    ['averageDuration', ['b', 'c', 'a']],
  ]) {
    assert.deepEqual(sortStatistics(entries, column, 'ascending').map((entry) => entry.endpoint), expected);
    assert.deepEqual(sortStatistics(entries, column, 'descending').map((entry) => entry.endpoint), [...expected].reverse());
  }
  assert.deepEqual(entries.map((entry) => entry.endpoint), ['a', 'b', 'c']);
});

test('aggregates every endpoint across dates before sorting by weighted average', () => {
  const rows = [
    { date: '2026-01-01', endpoint: 'a', requests: 1, readUnits: 2, writeUnits: 1, durationMs: 100 },
    { date: '2026-01-02', endpoint: 'a', requests: 9, readUnits: 4, writeUnits: 3, durationMs: 0 },
    { date: '2026-01-02', endpoint: 'b', requests: 1, readUnits: 1, writeUnits: 2, durationMs: 20 },
  ];
  const totals = aggregateStatistics(rows);
  assert.deepEqual(totals.find((item) => item.endpoint === 'a'), {
    endpoint: 'a', requests: 10, readUnits: 6, writeUnits: 4, durationMs: 100,
  });
  assert.deepEqual(sortStatistics(totals, 'averageDuration', 'ascending').map((item) => item.endpoint), ['a', 'b']);
  assert.deepEqual(aggregateStatistics([]), []);
  assert.deepEqual(aggregateStatistics(rows, '2026-01-01'), [
    { endpoint: 'a', requests: 1, readUnits: 2, writeUnits: 1, durationMs: 100 },
  ]);
  assert.equal(aggregateStatistics(rows, '2026-01-02').length, 2);
  assert.deepEqual(aggregateStatistics(rows, '2026-01-03'), []);
});
