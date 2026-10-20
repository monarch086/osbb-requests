import { inject, Injectable } from '@angular/core';
import { AuthService } from './auth.service';
import { UserFacingError } from './error-message';

export interface Admin { userId: string; email: string; displayName: string; isActive: boolean; isSuperAdmin: boolean; version: number }
export interface Registration { id: string; firstName: string; lastName: string; phone: string; updatedAt: string; version: number }
export interface Ticket { id: number; ownerId: number; category: string; location: string; description: string; createdAt: string; status: string; visibility: string; resolution: string | null; assignedTo: string | null; priority: string; plannedDate: string | null }
export interface ManagedTicket { request: Ticket; version: number }
export interface Page<T> { items: T[]; cursor: string | null }
@Injectable({ providedIn: 'root' })
export class ApiService {
  private auth = inject(AuthService);
  async request<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
    const token = await this.auth.accessToken();
    const response = await fetch(this.auth.config!.apiUrl.replace(/\/$/, '') + '/admin' + path, {
      method, headers: { Authorization: 'Bearer ' + token, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }), cache: 'no-store', credentials: 'omit'
    });
    const result = await response.json().catch(() => ({})) as { error?: string };
    if (!response.ok) throw new UserFacingError(result.error || (response.status === 401 ? 'Сеанс завершено. Увійдіть ще раз.' : 'Не вдалося виконати дію. Спробуйте ще раз.'));
    return result as T;
  }
}
