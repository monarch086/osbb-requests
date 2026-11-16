import type { DailyStatistic } from '../api.models';

export type StatisticsSortColumn = 'requests' | 'readUnits' | 'writeUnits' | 'averageDuration';
export type SortDirection = 'ascending' | 'descending';

export function sortStatistics(
  items: DailyStatistic[],
  column: StatisticsSortColumn,
  direction: SortDirection,
): DailyStatistic[] {
  const value = (item: DailyStatistic): number =>
    column === 'averageDuration'
      ? item.requests > 0
        ? item.durationMs / item.requests
        : 0
      : item[column];
  const sign = direction === 'ascending' ? 1 : -1;
  return [...items].sort(
    (left, right) =>
      (value(left) - value(right)) * sign ||
      right.date.localeCompare(left.date) ||
      left.endpoint.localeCompare(right.endpoint),
  );
}
