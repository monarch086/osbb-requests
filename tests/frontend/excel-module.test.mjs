import assert from 'node:assert/strict';
import test from 'node:test';
import { resolveExcelModule } from '../../src/Frontend/src/app/export/excel-module.ts';

test('loads Workbook from both production CommonJS interop and development exports', () => {
  class Workbook {}
  assert.equal(resolveExcelModule({ default: { Workbook } }).Workbook, Workbook);
  assert.equal(resolveExcelModule({ Workbook }).Workbook, Workbook);
});
