import type { ManagedTicket, RegisteredResident, Registration } from '../api.models';
import { residentTypeLabel } from '../residents/resident-type';
import {
  floorLabel,
  formatDate,
  requestLocationLabel,
  statusLabels,
  ticketPriorities,
} from '../shared/ticket-presentation';

export const ticketHeadings = [
  'Номер',
  'Дата створення',
  'Статус',
  'Категорія',
  'Місце',
  'Поверх',
  'Уточнення місця',
  'Опис',
  'Пріоритет',
  'Видимість',
  'Відповідальний',
  'Планова дата',
  'Пояснення',
];

export const residentHeadings = [
  'Прізвище',
  'Ім’я',
  'Тип мешканця',
  'Телефон',
  'Email',
  'Будинок',
  'Квартира',
  'Telegram Chat ID',
];

export function ticketRows(tickets: ManagedTicket[]): (string | number)[][] {
  return tickets.map(({ request: r }) => [
    String(r.id),
    formatDate(r.createdAt),
    statusLabels[r.status],
    r.category,
    requestLocationLabel(r),
    r.floor == null ? '' : floorLabel(r.floor),
    r.location || '',
    r.description,
    ticketPriorities.find((priority) => priority.value === r.priority)?.label ?? r.priority,
    r.visibility === 'Public' ? 'Для мешканців комплексу' : 'Приватна',
    r.assignedTo ?? '',
    r.plannedDate ?? '',
    r.resolution ?? '',
  ]);
}

export function registeredResidentRows(residents: RegisteredResident[]): (string | number)[][] {
  return residents.map((r) =>
    r.personalDataDeleted
      ? [
          'Мешканець видалив свої дані',
          '',
          '',
          '',
          '',
          '',
          '',
          r.telegramChatId,
          formatDate(r.registeredAt),
        ]
      : [
          r.lastName ?? '',
          r.firstName ?? '',
          residentTypeLabel(r.residentType),
          r.phone ?? '',
          r.email ?? '',
          r.buildingNumber ?? '',
          r.apartmentNumber ?? '',
          r.telegramChatId,
          formatDate(r.registeredAt),
        ],
  );
}

export function pendingResidentRows(residents: Registration[]): (string | number)[][] {
  return residents.map((r) => [
    r.lastName,
    r.firstName,
    residentTypeLabel(r.residentType),
    r.phone,
    r.email ?? '',
    r.buildingNumber ?? '',
    r.apartmentNumber ?? '',
    r.telegramChatId,
    formatDate(r.updatedAt),
  ]);
}
