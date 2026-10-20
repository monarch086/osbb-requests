export interface TicketFilters {
  status: string;
  category: string;
  search: string;
  from: string;
  to: string;
}
export const ticketStatuses = ['New', 'InProgress', 'AwaitingClarification', 'Completed', 'Rejected'];
export const ticketCategories = ['сантехніка', 'електрика', 'прибирання', 'місця загального користування', 'інше'];
export const statusLabels: Record<string, string> = {
  New: 'Нова', InProgress: 'В роботі', AwaitingClarification: 'Очікує уточнення',
  Completed: 'Виконана', Rejected: 'Відхилена'
};
const dateFormatter = new Intl.DateTimeFormat('uk-UA', {
  dateStyle: 'short', timeStyle: 'short', timeZone: 'Europe/Kyiv'
});
export function formatDate(value: string): string {
  return dateFormatter.format(new Date(value));
}
