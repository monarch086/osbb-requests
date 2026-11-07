import type * as ExcelJS from 'exceljs';

export function resolveExcelModule(module: typeof ExcelJS): typeof ExcelJS {
  return (module as unknown as { default?: typeof ExcelJS }).default ?? module;
}
