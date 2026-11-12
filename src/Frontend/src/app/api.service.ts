import { inject, Injectable } from '@angular/core';

import type {
  Admin,
  Assignee,
  BroadcastMessage,
  ManagedTicket,
  MessageChange,
  NotificationSettings,
  Page,
  PagePermissions,
  RegisteredResident,
  Registration,
  ResidentDataChange,
  ResidentTypeHistory,
  Ticket,
  TicketFilters,
  TicketHistory,
} from './api.models';
import { AuthService } from './auth.service';
import { UserFacingError } from './error-message';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly auth = inject(AuthService);
  updateAdminPermissions(admin: Admin, permissions: PagePermissions): Promise<{ ok: boolean }> {
    return this.request('/administrators/' + admin.userId + '/permissions', 'PATCH', {
      version: admin.version,
      permissions,
    });
  }
  getCurrentAdmin(): Promise<Admin> {
    return this.request('/me');
  }

  getAssignees(cursor: string | null): Promise<Page<Assignee>> {
    return this.request('/assignees' + this.cursorQuery(cursor));
  }

  getResidents(cursor: string | null): Promise<Page<RegisteredResident>> {
    return this.request('/residents' + this.cursorQuery(cursor));
  }

  getRegistrationCount(): Promise<{ count: number }> {
    return this.request('/registrations/count');
  }

  getRegistrations(cursor: string | null): Promise<Page<Registration>> {
    return this.request('/registrations' + this.cursorQuery(cursor));
  }
  updateResidentData(
    resident: Registration | RegisteredResident,
    change: ResidentDataChange,
  ): Promise<{ ok: boolean }> {
    return this.request('/registrations/' + resident.id + '/data', 'PATCH', {
      version: resident.version,
      ...change,
    });
  }
  getResidentHistory(id: string, cursor: string | null): Promise<Page<ResidentTypeHistory>> {
    return this.request('/registrations/' + id + '/history' + this.cursorQuery(cursor));
  }

  getAdministrators(cursor: string | null): Promise<Page<Admin>> {
    return this.request('/administrators' + this.cursorQuery(cursor));
  }

  getMessages(cursor: string | null): Promise<Page<BroadcastMessage>> {
    return this.request('/messages' + this.cursorQuery(cursor));
  }

  getTickets(filters: TicketFilters, cursor: string | null): Promise<Page<ManagedTicket>> {
    return this.request('/requests?' + this.ticketFilterQuery(filters, cursor));
  }

  async exportTickets(filters: TicketFilters): Promise<Blob> {
    const response = await this.fetch('/requests/export?' + this.ticketFilterQuery(filters, null));
    await this.checkResponse(response);
    return response.blob();
  }

  private ticketFilterQuery(filters: TicketFilters, cursor: string | null): string {
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
    return params.toString();
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
    const response = await this.fetch(path, method, body);
    await this.checkResponse(response);
    return (await response.json().catch(() => ({}))) as T;
  }

  private async checkResponse(response: Response): Promise<void> {
    if (!response.ok) {
      if (response.status === 429) {
        throw new UserFacingError('Забагато запитів. Зачекайте кілька секунд і спробуйте ще раз.');
      }
      const result = (await response.json().catch(() => ({}))) as { error?: string };
      throw new UserFacingError(
        result.error ||
          (response.status === 401
            ? 'Сеанс завершено. Увійдіть ще раз.'
            : 'Не вдалося виконати дію. Спробуйте ще раз.'),
      );
    }
  }

  private async fetch(path: string, method = 'GET', body?: unknown): Promise<Response> {
    const token = await this.auth.accessToken();
    return fetch(this.auth.config!.apiUrl.replace(/\/$/, '') + '/admin' + path, {
      method,
      headers: {
        Authorization: 'Bearer ' + token,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      cache: 'no-store',
      credentials: 'omit',
    });
  }
}
