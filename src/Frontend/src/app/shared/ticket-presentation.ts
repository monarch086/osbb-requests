import type { Ticket, TicketPriority, TicketStatus } from '../api.models';

export type { TicketFilters } from '../api.models';
export const ticketStatuses: TicketStatus[] = [
  'New',
  'InProgress',
  'AwaitingClarification',
  'Completed',
  'Rejected',
];
export const ticketCategories = [
  'сантехніка',
  'електрика',
  'прибирання',
  'місця загального користування',
  'інше',
];
export const statusLabels: Record<TicketStatus, string> = {
  New: 'Нова',
  InProgress: 'В роботі',
  AwaitingClarification: 'Очікує уточнення',
  Completed: 'Виконана',
  Rejected: 'Відхилена',
};
const dateFormatter = new Intl.DateTimeFormat('uk-UA', {
  dateStyle: 'short',
  timeStyle: 'short',
  timeZone: 'Europe/Kyiv',
});
export function formatDate(value: string): string {
  return dateFormatter.format(new Date(value));
}

export const buildingNumbers = ['4Б', '4В', '4Г', '4Д'];
export const requestTargets = [
  { value: 'Complex', label: 'Весь комплекс' },
  ...buildingNumbers.map((number) => ({ value: `Building${number}`, label: `Будинок ${number}` })),
];
export const entranceOptions = [
  { value: null, label: 'Не зазначено' },
  { value: 1, label: '1' },
  { value: 2, label: '2' },
];
export const buildingFloors = [0, ...Array.from({ length: 25 }, (_, index) => index + 1)];
export function floorLabel(floor: number): string {
  return floor === 0 ? 'Парадне' : floor.toString();
}
export function requestTarget(ticket: Ticket): string | null {
  return ticket.scope === 'Complex'
    ? 'Complex'
    : ticket.scope === 'Building' && ticket.buildingNumber
      ? `Building${ticket.buildingNumber}`
      : null;
}
export function requestLocationLabel(ticket: Ticket): string {
  if (ticket.scope === 'Complex') {
    return 'Весь комплекс';
  }
  if (ticket.scope === 'Building') {
    return (
      `Будинок ${ticket.buildingNumber} · під’їзд ${ticket.entrance ?? 'Не зазначено'}` +
      (ticket.floor === 0
        ? ' · Парадне'
        : ticket.floor
          ? ` · поверх ${ticket.floor}`
          : ' · без поверху')
    );
  }
  return 'Будинок не уточнено';
}

export const ticketPriorities: { value: TicketPriority; label: string; badgeClass: string }[] = [
  { value: 'Low', label: 'Низький', badgeClass: 'text-bg-secondary' },
  { value: 'Normal', label: 'Звичайний', badgeClass: 'text-bg-light' },
  { value: 'High', label: 'Високий', badgeClass: 'text-bg-warning' },
  { value: 'Urgent', label: 'Терміновий', badgeClass: 'text-bg-danger' },
];
