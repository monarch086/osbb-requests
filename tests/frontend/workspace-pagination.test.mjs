import assert from 'node:assert/strict';
import test from 'node:test';
import { WorkspacePagination } from '../../src/Frontend/src/app/workspace/workspace-pagination.ts';

test('remembers page cursors and discards stale following pages after a refresh', () => {
  const pages = new WorkspacePagination();
  pages.accept(0, null, 'a');
  pages.accept(1, 'a', 'b');
  pages.accept(2, 'b', 'c');
  assert.equal(pages.availablePages, 4);
  pages.accept(1, 'a', 'changed');
  assert.equal(pages.index, 1);
  assert.equal(pages.availablePages, 3);
  assert.equal(pages.start(2), 'changed');
  assert.equal(pages.start(3), undefined);
  pages.accept(2, 'changed', null);
  assert.equal(pages.cursor, null);
  pages.reset();
  assert.equal(pages.index, 0);
  assert.equal(pages.start(0), null);
  assert.equal(pages.availablePages, 1);
});
