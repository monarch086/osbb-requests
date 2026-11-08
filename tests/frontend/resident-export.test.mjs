import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildSync } from '../../src/Frontend/node_modules/esbuild/lib/main.js';

const bundled = buildSync({
  entryPoints: [fileURLToPath(new URL('../../src/Frontend/src/app/export/export-data.ts', import.meta.url))],
  bundle: true,
  write: false,
  platform: 'node',
  format: 'esm',
});
const data = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString('base64')}`);

test('resident exports include the localized type after the name', () => {
  const resident = { firstName: 'Ім’я', lastName: 'Прізвище', phone: '', telegramChatId: '1', registeredAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' };
  assert.equal(data.residentHeadings[2], 'Тип мешканця');
  assert.equal(data.registeredResidentRows([{ ...resident, residentType: 'Owner' }])[0][2], 'Власник');
  assert.equal(data.registeredResidentRows([resident])[0][2], 'Член родини');
  assert.equal(data.pendingResidentRows([{ ...resident, residentType: 'Tenant' }])[0][2], 'Орендар');
});
