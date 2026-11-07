import assert from 'node:assert/strict';
import test from 'node:test';
import { exportFilename } from '../../src/Frontend/src/app/export/export-filename.ts';

test('includes the current Kyiv date in each Excel filename', () => {
  const time = new Date('2026-11-07T22:30:00Z');
  assert.equal(exportFilename('Заявки', time), 'Заявки-08-11-2026.xlsx');
  assert.equal(exportFilename('Мешканці', time), 'Мешканці-08-11-2026.xlsx');
  assert.equal(exportFilename('Очікують перевірки', time), 'Очікують перевірки-08-11-2026.xlsx');
});

test('uses Kyiv daylight saving time and pads dates', () => {
  assert.equal(exportFilename('Заявки', new Date('2026-05-01T21:30:00Z')), 'Заявки-02-05-2026.xlsx');
  assert.equal(exportFilename('Заявки', new Date('2026-12-31T22:30:00Z')), 'Заявки-01-01-2027.xlsx');
});
