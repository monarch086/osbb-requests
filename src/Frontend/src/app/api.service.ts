import { inject, Injectable } from '@angular/core';

import { AuthService } from './auth.service';
import { UserFacingError } from './error-message';

export interface MessageChange {
  id: string;
  text: string;
}
export interface BroadcastMessage extends MessageChange {
  createdBy: string;
  createdById: string;
  createdAt: string;
}
export interface NotificationSettings {
  telegramChatId: string | null;
  notifyNewResidents: boolean;
  notifyNewRequests: boolean;
  notifyNewAdministrators: boolean;
}
export interface Admin extends NotificationSettings {
  userId: string;
  email: string;
  displayName: string;
  isActive: boolean;
  isSuperAdmin: boolean;
  version: number;
}
export interface Registration {
  telegramChatId: string;
  id: string;
  firstName: string;
  lastName: string;
  phone: string;
  buildingNumber?: string | null;
  apartmentNumber?: string | null;
  updatedAt: string;
  version: number;
}
export interface RegisteredResident {
  telegramChatId: string;
  id: string;
  firstName: string | null;
  lastName: string | null;
  phone: string | null;
  buildingNumber?: string | null;
  apartmentNumber?: string | null;
  personalDataDeleted?: boolean;
  registeredAt: string;
}
export type TicketStatus =
  'New' | 'InProgress' | 'AwaitingClarification' | 'Completed' | 'Rejected';
export type TicketPriority = 'Low' | 'Normal' | 'High' | 'Urgent';

export interface Ticket {
  id: number;
  ownerId: number;
  scope: 'Complex' | 'Building' | null;
  buildingNumber: string | null;
  floor: number | null;
  entrance: number | null;
  category: string;
  location: string;
  description: string;
  createdAt: string;
  status: TicketStatus;
  visibility: string;
  resolution: string | null;
  assignedTo: string | null;
  priority: TicketPriority;
  plannedDate: string | null;
}
export interface RequestAuthor {
  personalDataDeleted?: boolean;
  firstName: string | null;
  lastName: string | null;
  buildingNumber: string | null;
  apartmentNumber: string | null;
}
export interface ManagedTicket {
  request: Ticket;
  author?: RequestAuthor | null;
  version: number;
}
export interface Page<T> {
  items: T[];
  cursor: string | null;
}
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly auth = inject(AuthService);
  async request<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
    const token = await this.auth.accessToken();
    const response = await fetch(this.auth.config!.apiUrl.replace(/\/$/, '') + '/admin' + path, {
      method,
      headers: {
        Authorization: 'Bearer ' + token,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      cache: 'no-store',
      credentials: 'omit',
    });
    const result = (await response.json().catch(() => ({}))) as { error?: string };
    if (!response.ok) {
      throw new UserFacingError(
        result.error ||
          (response.status === 401
            ? 'Сеанс завершено. Увійдіть ще раз.'
            : 'Не вдалося виконати дію. Спробуйте ще раз.'),
      );
    }
    return result as T;
  }
}
