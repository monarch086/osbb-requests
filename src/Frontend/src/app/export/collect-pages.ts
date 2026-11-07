import type { Page } from '../api.models';

export async function collectPages<T>(
  fetchPage: (cursor: string | null) => Promise<Page<T>>,
  isCurrent: () => boolean,
): Promise<T[] | null> {
  const items: T[] = [];
  const visited = new Set<string>();
  let cursor: string | null = null;
  do {
    const page = await fetchPage(cursor);
    if (!isCurrent()) {
      return null;
    }
    items.push(...page.items);
    cursor = page.cursor;
    if (cursor && visited.has(cursor)) {
      throw new Error('Repeated export cursor');
    }
    if (cursor) {
      visited.add(cursor);
    }
  } while (cursor);
  return items;
}
