import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildSync } from '../../src/Frontend/node_modules/esbuild/lib/main.js';
import { ResidentDirectory } from '../../src/Frontend/src/app/residents/resident-directory.ts';

const bundled = buildSync({
  entryPoints: [fileURLToPath(new URL('../../src/Frontend/src/app/export/export-data.ts', import.meta.url))],
  bundle: true,
  write: false,
  platform: 'node',
  format: 'esm',
});
const data = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString('base64')}`);

test('export rows contain all filtered residents across pages', () => {
  const directory = new ResidentDirectory();
  directory.setItems(Array.from({ length: 65 }, (_, i) => ({
    id: String(i), telegramChatId: String(i), firstName: 'Ім’я', lastName: 'Прізвище',
    phone: '', registeredAt: '2026-01-01T00:00:00Z',
    residentType: i < 35 ? 'Owner' : 'Tenant', buildingNumber: '4Б',
  })));
  directory.applyFilters({ ...directory.filters, residentType: 'Owner', building: '4Б' });
  const rows = data.registeredResidentRows(directory.all());
  assert.equal(directory.page(0).length, 25);
  assert.equal(directory.page(1).length, 10);
  assert.equal(rows.length, 35);
  assert.ok(rows.every(row => row[2] === 'Власник'));
});
test('resident exports include the localized type after the name', () => {
  const resident = { firstName: 'Ім’я', lastName: 'Прізвище', phone: '', telegramChatId: '1', registeredAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' };
  assert.equal(data.residentHeadings[2], 'Тип мешканця');
  assert.equal(data.registeredResidentRows([{ ...resident, residentType: 'Owner' }])[0][2], 'Власник');
  assert.equal(data.registeredResidentRows([resident])[0][2], 'Член родини');
  assert.equal(data.pendingResidentRows([{ ...resident, residentType: 'Tenant' }])[0][2], 'Орендар');
});

test('resident exports omit Telegram IDs while retaining aligned dates', () => {
  const resident = { firstName: 'Ім’я', lastName: 'Прізвище', phone: '', telegramChatId: '123456789', registeredAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' };
  assert.ok(data.residentHeadings.every(heading => !heading.includes('Telegram')));
  const rows = [
    data.registeredResidentRows([resident])[0],
    data.registeredResidentRows([{ ...resident, personalDataDeleted: true }])[0],
    data.pendingResidentRows([resident])[0],
  ];
  for (const row of rows) {
    assert.equal(row.length, data.residentHeadings.length + 1);
    assert.ok(!row.includes(resident.telegramChatId));
    assert.ok(row.at(-1));
  }
});
