import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const frontend = fileURLToPath(new URL('../../src/Frontend/', import.meta.url));
const require = createRequire(new URL('../../src/Frontend/package.json', import.meta.url));
const angularRequire = createRequire(require.resolve('@angular/build/package.json'));
const { buildSync } = angularRequire('esbuild');
const bundle = buildSync({
  stdin: {
    contents: `export { RequestsList } from './requests/requests-list';
      export { ResidentsList } from './residents/residents-list';
      export { MessagesList } from './messages/messages-list';
      export { AdministratorsList } from './administrators/administrators-list';`,
    resolveDir: frontend + 'src/app',
    loader: 'ts',
  },
  bundle: true,
  format: 'esm',
  platform: 'node',
  write: false,
}).outputFiles[0].text;
const { RequestsList, ResidentsList, MessagesList, AdministratorsList } =
  await import('data:text/javascript;base64,' + Buffer.from(bundle).toString('base64'));

test('request filters are captured independently and reset page cursors', () => {
  const state = new RequestsList();
  state.pagination.accept(0, null, 'next');
  const filters = { status: ['Completed'], category: 'ліфт', search: '', from: '', to: '', target: '', floor: '' };
  state.applyTicketFilters(filters);
  filters.status.push('New');
  state.filters.status.push('Rejected');
  assert.deepEqual(state.appliedFilters.status, ['Completed']);
  assert.equal(state.appliedFilters.category, 'ліфт');
  assert.equal(state.pagination.availablePages, 1);
});

test('resident, message and administrator states are independent', () => {
  const residents = new ResidentsList();
  residents.registeredResidents = Array.from({ length: 25 }, (_, i) => ({
    id: String(i), telegramChatId: String(i), firstName: 'Тест', lastName: 'Тестова',
    buildingNumber: '4Б', registeredAt: '2026-01-01T00:00:00Z',
  }));
  residents.total = 30;
  assert.equal(residents.availablePages(), 2);
  assert.equal(residents.itemCount(), 25);
  assert.equal(residents.hasNext(), true);
  residents.residentView = 'pending';
  residents.resetPagination();
  assert.equal(residents.itemCount(), 0);
  assert.equal(residents.availablePages(), 1);

  const messages = new MessagesList();
  const admins = new AdministratorsList();
  messages.messages.push({ id: 'message' });
  admins.admins.push({ userId: 'admin' });
  messages.resetPagination();
  assert.equal(messages.itemCount(), 0);
  assert.equal(admins.itemCount(), 1);
});

test('pending pagination derives its page count from the server total', () => {
  const residents = new ResidentsList();
  residents.residentView = 'pending';
  residents.total = 26;
  residents.pagination.index = 1;
  residents.residents = [{ id: '25', buildingNumber: '4Б' }];
  assert.equal(residents.itemCount(), 1);
  assert.equal(residents.availablePages(), 2);
  assert.equal(residents.hasNext(), false);
});
