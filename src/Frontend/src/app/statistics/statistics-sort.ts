import type { DailyStatistic } from '../api.models';

export type StatisticsSortColumn =
  'requests' | 'readUnits' | 'readUnitsPerRequest' | 'writeUnits' | 'averageDuration';
export type SortDirection = 'ascending' | 'descending';
export type EndpointStatistic = Omit<DailyStatistic, 'date'>;

export function readUnitsPerRequest(item: EndpointStatistic): number {
  return item.requests > 0 ? item.readUnits / item.requests : 0;
}

export function aggregateStatistics(
  items: DailyStatistic[],
  date: string | null = null,
): EndpointStatistic[] {
  const endpoints = new Map<string, EndpointStatistic>();
  for (const item of items) {
    if (date !== null && item.date !== date) {
      continue;
    }
    const current = endpoints.get(item.endpoint) ?? {
      endpoint: item.endpoint,
      requests: 0,
      readUnits: 0,
      writeUnits: 0,
      durationMs: 0,
    };
    current.requests += item.requests;
    current.readUnits += item.readUnits;
    current.writeUnits += item.writeUnits;
    current.durationMs += item.durationMs;
    endpoints.set(item.endpoint, current);
  }
  return [...endpoints.values()];
}

export function sortStatistics(
  items: EndpointStatistic[],
  column: StatisticsSortColumn,
  direction: SortDirection,
): EndpointStatistic[] {
  const value = (item: EndpointStatistic): number => {
    if (column === 'readUnitsPerRequest') {
      return readUnitsPerRequest(item);
    }
    if (column === 'averageDuration') {
      return item.requests > 0 ? item.durationMs / item.requests : 0;
    }
    return item[column];
  };
  const sign = direction === 'ascending' ? 1 : -1;
  return [...items].sort(
    (left, right) =>
      (value(left) - value(right)) * sign || left.endpoint.localeCompare(right.endpoint),
  );
}
