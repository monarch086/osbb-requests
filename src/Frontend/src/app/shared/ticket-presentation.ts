import type { Ticket } from '../api.service';

export interface TicketFilters {
  target: string;
  floor: string;
  status: string[];
  category: string;
  search: string;
  from: string;
  to: string;
}
export const ticketStatuses = [
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
export const statusLabels: Record<string, string> = {
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
  if (ticket.scope === 'Complex') return 'Весь комплекс';
  if (ticket.scope === 'Building')
    return (
      `Будинок ${ticket.buildingNumber}` +
      (ticket.floor === 0
        ? ' · Парадне'
        : ticket.floor
          ? ` · поверх ${ticket.floor}`
          : ' · без поверху')
    );
  return 'Будинок не уточнено';
}
