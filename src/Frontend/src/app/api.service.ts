import { inject, Injectable } from '@angular/core';

import type {
  Admin,
  Assignee,
  BroadcastMessage,
  ManagedTicket,
  MessageChange,
  NotificationSettings,
  Page,
  RegisteredResident,
  Registration,
  Ticket,
  TicketFilters,
  TicketHistory,
} from './api.models';
import { AuthService } from './auth.service';
import { UserFacingError } from './error-message';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly auth = inject(AuthService);
  getCurrentAdmin(): Promise<Admin> {
    return this.request('/me');
  }

  getAssignees(cursor: string | null): Promise<Page<Assignee>> {
    return this.request('/assignees' + this.cursorQuery(cursor));
  }

  getResidents(cursor: string | null): Promise<Page<RegisteredResident>> {
    return this.request('/residents' + this.cursorQuery(cursor));
  }

  getRegistrations(cursor: string | null): Promise<Page<Registration>> {
    return this.request('/registrations' + this.cursorQuery(cursor));
  }

  getAdministrators(cursor: string | null): Promise<Page<Admin>> {
    return this.request('/administrators' + this.cursorQuery(cursor));
  }

  getMessages(cursor: string | null): Promise<Page<BroadcastMessage>> {
    return this.request('/messages' + this.cursorQuery(cursor));
  }

  getTickets(filters: TicketFilters, cursor: string | null): Promise<Page<ManagedTicket>> {
    const params = new URLSearchParams();
    if (cursor) {
      params.set('cursor', cursor);
    }
    const { status, ...rest } = filters;
    if (status.length) {
      params.set('status', status.join(','));
    }
    for (const [key, value] of Object.entries(rest)) {
      if (value) {
        params.set(key, value);
      }
    }
    return this.request('/requests?' + params.toString());
  }

  sendMessage(change: MessageChange): Promise<BroadcastMessage> {
    return this.request('/messages', 'POST', change);
  }

  updateNotificationSettings(admin: Admin, settings: NotificationSettings): Promise<void> {
    return this.request(
      '/administrators/' + encodeURIComponent(admin.userId) + '/notifications',
      'PATCH',
      {
        ...settings,
        version: admin.version,
      },
    );
  }

  updateAdminAccess(admin: Admin): Promise<void> {
    return this.request('/administrators/' + encodeURIComponent(admin.userId), 'PATCH', {
      isActive: !admin.isActive,
      version: admin.version,
    });
  }

  decideRegistration(
    registration: Registration,
    approved: boolean,
    reason: string | null,
  ): Promise<void> {
    return this.request('/registrations/' + registration.id + '/decision', 'POST', {
      approved,
      reason,
      version: registration.version,
    });
  }

  getTicket(ticket: ManagedTicket): Promise<ManagedTicket> {
    return this.request(this.ticketPath(ticket));
  }

  getTicketHistory(ticket: ManagedTicket): Promise<TicketHistory> {
    return this.request(this.ticketPath(ticket) + '/history');
  }

  updateTicket(ticket: ManagedTicket, draft: Ticket): Promise<void> {
    return this.request(this.ticketPath(ticket), 'PATCH', {
      scope: draft.scope,
      buildingNumber: draft.buildingNumber,
      floor: draft.floor,
      entrance: draft.entrance,
      status: draft.status,
      visibility: draft.visibility,
      resolution: draft.resolution,
      assignedTo: draft.assignedTo,
      priority: draft.priority,
      plannedDate: draft.plannedDate || null,
      version: ticket.version,
    });
  }

  private cursorQuery(cursor: string | null): string {
    return cursor ? '?cursor=' + encodeURIComponent(cursor) : '';
  }

  private ticketPath(ticket: ManagedTicket): string {
    return '/requests/' + ticket.request.ownerId + '/' + ticket.request.id;
  }

  private async request<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
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
