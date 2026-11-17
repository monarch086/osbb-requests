import type { DailyStatistic } from '../api.models';

export type StatisticsSortColumn = 'requests' | 'readUnits' | 'writeUnits' | 'averageDuration';
export type SortDirection = 'ascending' | 'descending';
export type EndpointStatistic = Omit<DailyStatistic, 'date'>;

export function aggregateStatistics(items: DailyStatistic[]): EndpointStatistic[] {
  const endpoints = new Map<string, EndpointStatistic>();
  for (const item of items) {
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
  const value = (item: EndpointStatistic): number =>
    column === 'averageDuration'
      ? item.requests > 0
        ? item.durationMs / item.requests
        : 0
      : item[column];
  const sign = direction === 'ascending' ? 1 : -1;
  return [...items].sort(
    (left, right) =>
      (value(left) - value(right)) * sign || left.endpoint.localeCompare(right.endpoint),
  );
}
