import type { ManagedTicket, Page, TicketFilters } from '../api.models';

export interface RequestCounts {
  total: number;
  newCount: number;
  progressCount: number;
}

export async function countRequests(
  filters: TicketFilters,
  fetchPage: (filters: TicketFilters, cursor: string | null) => Promise<Page<ManagedTicket>>,
  isCurrent: () => boolean,
  firstPage?: Page<ManagedTicket>,
): Promise<RequestCounts | null> {
  const counts: RequestCounts = { total: 0, newCount: 0, progressCount: 0 };
  const visited = new Set<string>();
  let cursor: string | null = null;
  do {
    const page: Page<ManagedTicket> =
      cursor === null && firstPage ? firstPage : await fetchPage(filters, cursor);
    if (!isCurrent()) {
      return null;
    }
    counts.total += page.items.length;
    counts.newCount += page.items.filter((item) => item.request.status === 'New').length;
    counts.progressCount += page.items.filter(
      (item) => item.request.status === 'InProgress',
    ).length;
    cursor = page.cursor;
    if (cursor && visited.has(cursor)) {
      throw new Error('Repeated request cursor');
    }
    if (cursor) {
      visited.add(cursor);
    }
  } while (cursor);
  return counts;
}
