import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildSync } from '../../src/Frontend/node_modules/esbuild/lib/main.js';
import { loadTicketAuthors } from '../../src/Frontend/src/app/export/load-ticket-authors.ts';

const bundled = buildSync({
  entryPoints: [fileURLToPath(new URL('../../src/Frontend/src/app/export/export-data.ts', import.meta.url))],
  bundle: true, write: false, platform: 'node', format: 'esm',
});
const data = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString('base64')}`);
const ticket = (id, ownerId) => ({ request: { id, ownerId, createdAt: '2026-01-01T00:00:00Z', status: 'New', category: 'Інше', description: 'Опис', priority: 'Normal' }, version: 1 });

test('exports author and phone in aligned columns and hides deleted personal data', () => {
  const name = data.ticketHeadings.indexOf('Автор');
  const phone = data.ticketHeadings.indexOf('Телефон');
  const tickets = [
    { ...ticket(1, 1), author: { firstName: 'Олена', lastName: 'Коваль', phone: '+380500000000' } },
    { ...ticket(2, 2), author: { personalDataDeleted: true, firstName: 'Таємниця', phone: '123' } },
    ticket(3, 3),
  ];
  const rows = data.ticketRows(tickets);
  assert.equal(rows[0][name], 'Олена Коваль');
  assert.equal(rows[0][phone], '+380500000000');
  assert.equal(rows[1][name], 'Мешканець видалив свої дані');
  assert.equal(rows[1][phone], '');
  assert.equal(rows[2][name], '');
  assert.equal(rows[2][phone], '');
  assert.ok(rows.every(row => row.length === data.ticketHeadings.length));
});

test('retrieves each author once across all pages with bounded concurrency', async () => {
  const tickets = Array.from({ length: 60 }, (_, i) => ticket(i, i % 30));
  let active = 0, maxActive = 0;
  const calls = [];
  const result = await loadTicketAuthors(tickets, async entry => {
    calls.push(entry.request.ownerId);
    maxActive = Math.max(maxActive, ++active);
    await new Promise(resolve => setTimeout(resolve, 1));
    active--;
    return { ...entry, author: { firstName: String(entry.request.ownerId), phone: '123' } };
  }, () => true);
  assert.equal(calls.length, 30);
  assert.ok(maxActive <= 8);
  assert.equal(result.length, 60);
  assert.equal(result[31].author.firstName, '1');
});

test('stops loading authors when the session becomes stale and propagates errors', async () => {
  let current = true;
  const tickets = Array.from({ length: 20 }, (_, i) => ticket(i, i));
  let calls = 0;
  const result = await loadTicketAuthors(tickets, async entry => { calls++; current = false; return entry; }, () => current);
  assert.equal(result, null);
  assert.ok(calls <= 8);
  await assert.rejects(loadTicketAuthors([ticket(1, 1)], async () => { throw new Error('unavailable'); }, () => true), /unavailable/);
});
