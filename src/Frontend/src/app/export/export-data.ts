import type { RegisteredResident, Registration } from '../api.models';
import { residentTypeLabel } from '../residents/resident-type';
import { formatDate } from '../shared/ticket-presentation';

export const residentHeadings = [
  'Прізвище',
  'Ім’я',
  'Тип мешканця',
  'Телефон',
  'Email',
  'Будинок',
  'Квартира',
];

export function registeredResidentRows(residents: RegisteredResident[]): (string | number)[][] {
  return residents.map((r) =>
    r.personalDataDeleted
      ? ['Мешканець видалив свої дані', '', '', '', '', '', '', formatDate(r.registeredAt)]
      : [
          r.lastName ?? '',
          r.firstName ?? '',
          residentTypeLabel(r.residentType),
          r.phone ?? '',
          r.email ?? '',
          r.buildingNumber ?? '',
          r.apartmentNumber ?? '',
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
    formatDate(r.updatedAt),
  ]);
}
