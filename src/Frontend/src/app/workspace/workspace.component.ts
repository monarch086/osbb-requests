import { Component, OnDestroy, OnInit, Output, EventEmitter, inject } from '@angular/core';
import { Subscription } from 'rxjs';
import { AuthService } from '../auth.service';
import { ApiService, Admin, Registration, ManagedTicket, Ticket, Page } from '../api.service';
import { errorMessage } from '../error-message';
import { RequestsComponent } from '../requests/requests.component';
import { ResidentsComponent } from '../residents/residents.component';
import { AdministratorsComponent } from '../administrators/administrators.component';
import { RegistrationRejectionDialogComponent } from '../registration-rejection-dialog/registration-rejection-dialog.component';
import { TicketDialogComponent } from '../ticket-dialog/ticket-dialog.component';

@Component({
  selector: 'app-workspace',
  imports: [
    RequestsComponent,
    ResidentsComponent,
    AdministratorsComponent,
    RegistrationRejectionDialogComponent,
    TicketDialogComponent,
  ],
  templateUrl: './workspace.component.html',
})
export class WorkspaceComponent implements OnInit, OnDestroy {
  @Output() savingChange = new EventEmitter<boolean>();
  private auth = inject(AuthService);
  private api = inject(ApiService);
  me: Admin | null = null;
  loading = false;
  saving = false;
  error = '';
  notice = '';
  tab: 'requests' | 'residents' | 'admins' = 'requests';
  tickets: ManagedTicket[] = [];
  residents: Registration[] = [];
  admins: Admin[] = [];
  assignees: {
    userId: string;
    email: string;
  }[] = [];
  cursor: string | null = null;
  selected: ManagedTicket | null = null;
  draft: Ticket | null = null;
  history: Record<string, string>[] = [];
  rejecting: Registration | null = null;
  filters = { status: '', category: '', search: '', from: '', to: '' };
  private subscription?: Subscription;
  private epoch = 0;
  ngOnInit(): void {
    this.subscription = this.auth.user$.subscribe((user) => {
      this.epoch++;
      this.clear();
      if (user) void this.workspace();
    });
  }
  ngOnDestroy(): void {
    this.epoch++;
    this.subscription?.unsubscribe();
    this.clear();
  }
  private clear(): void {
    this.tab = 'requests';
    this.filters = { status: '', category: '', search: '', from: '', to: '' };
    this.me = null;
    this.tickets = [];
    this.residents = [];
    this.admins = [];
    this.assignees = [];
    this.selected = null;
    this.draft = null;
    this.rejecting = null;
    this.history = [];
    this.cursor = null;
    this.notice = '';
  }
  async workspace(): Promise<void> {
    const epoch = this.epoch;
    this.loading = true;
    this.error = '';
    try {
      const user = await this.api.request<Admin>('/me');
      if (epoch !== this.epoch) return;
      this.me = user;
      if (!user.isSuperAdmin && this.tab === 'admins') this.tab = 'requests';
      if (user.isActive) {
        const assignees: {
          userId: string;
          email: string;
        }[] = [];
        let cursor: string | null = null;
        do {
          const page: Page<{
            userId: string;
            email: string;
          }> = await this.api.request(
            '/assignees' + (cursor ? '?cursor=' + encodeURIComponent(cursor) : ''),
          );
          assignees.push(...page.items);
          cursor = page.cursor;
        } while (cursor && epoch === this.epoch);
        if (epoch !== this.epoch) return;
        this.assignees = assignees;
        await this.load();
      }
    } catch (e) {
      if (epoch === this.epoch) this.error = errorMessage(e);
    } finally {
      if (epoch === this.epoch) this.loading = false;
    }
  }
  async switchTab(tab: 'requests' | 'residents' | 'admins'): Promise<void> {
    if (this.saving || this.loading || (tab === 'admins' && !this.me?.isSuperAdmin)) return;
    this.tab = tab;
    this.selected = null;
    this.draft = null;
    this.rejecting = null;
    this.notice = '';
    await this.load();
  }
  async load(more = false): Promise<void> {
    if (!this.me?.isActive || this.saving) return;
    const epoch = this.epoch;
    const tab = this.tab;
    this.loading = true;
    this.error = '';
    const params = new URLSearchParams();
    if (more && this.cursor) params.set('cursor', this.cursor);
    if (tab === 'requests')
      for (const [key, value] of Object.entries(this.filters)) if (value) params.set(key, value);
    const path =
      tab === 'requests' ? '/requests' : tab === 'residents' ? '/registrations' : '/administrators';
    try {
      const page = await this.api.request<Page<ManagedTicket | Registration | Admin>>(
        path + '?' + params.toString(),
      );
      if (epoch !== this.epoch || tab !== this.tab) return;
      if (tab === 'requests')
        this.tickets = [...(more ? this.tickets : []), ...(page.items as ManagedTicket[])];
      if (tab === 'residents')
        this.residents = [...(more ? this.residents : []), ...(page.items as Registration[])];
      if (tab === 'admins')
        this.admins = [...(more ? this.admins : []), ...(page.items as Admin[])];
      this.cursor = page.cursor;
    } catch (e) {
      if (epoch === this.epoch) this.error = errorMessage(e);
    } finally {
      if (epoch === this.epoch) this.loading = false;
    }
  }
  async setAccess(user: Admin): Promise<void> {
    if (!this.me?.isSuperAdmin || user.isSuperAdmin) return;
    await this.write(async () => {
      await this.api.request('/administrators/' + encodeURIComponent(user.userId), 'PATCH', {
        isActive: !user.isActive,
        version: user.version,
      });
      user.isActive = !user.isActive;
      user.version++;
      this.notice = 'Доступ адміністратора оновлено.';
    });
  }
  async decide(registration: Registration, approved: boolean, reason = ''): Promise<void> {
    if (!approved && !reason.trim()) {
      this.error = 'Вкажіть причину відхилення.';
      return;
    }
    await this.write(async () => {
      await this.api.request('/registrations/' + registration.id + '/decision', 'POST', {
        approved,
        reason: approved ? null : reason.trim(),
        version: registration.version,
      });
      this.residents = this.residents.filter((r) => r.id !== registration.id);
      this.rejecting = null;
      this.notice = approved ? 'Мешканцю надано доступ.' : 'Реєстрацію відхилено.';
    });
  }
  async open(ticket: ManagedTicket): Promise<void> {
    if (this.saving) return;
    this.selected = ticket;
    this.draft = { ...ticket.request };
    this.history = [];
    this.error = '';
    const epoch = this.epoch;
    try {
      const data = await this.api.request<{
        items: Record<string, string>[];
      }>(this.ticketPath(ticket) + '/history');
      if (epoch === this.epoch && this.selected === ticket) this.history = data.items;
    } catch (e) {
      if (epoch === this.epoch) this.error = errorMessage(e);
    }
  }
  async saveTicket(draft: Ticket): Promise<void> {
    const selected = this.selected;
    if (!selected) return;
    await this.write(async () => {
      await this.api.request(this.ticketPath(selected), 'PATCH', {
        status: draft.status,
        visibility: draft.visibility,
        resolution: draft.resolution,
        assignedTo: draft.assignedTo,
        priority: draft.priority,
        plannedDate: draft.plannedDate || null,
        version: selected.version,
      });
      selected.request = { ...draft };
      selected.version++;
      this.selected = null;
      this.draft = null;
      this.notice = 'Зміни заявки збережено.';
    });
  }
  private ticketPath(ticket: ManagedTicket): string {
    return '/requests/' + ticket.request.ownerId + '/' + ticket.request.id;
  }
  private async write(action: () => Promise<void>): Promise<void> {
    if (this.saving) return;
    this.saving = true;
    this.savingChange.emit(true);
    this.error = '';
    this.notice = '';
    try {
      await action();
    } catch (e) {
      this.error = errorMessage(e);
    } finally {
      this.saving = false;
      this.savingChange.emit(false);
    }
  }
  closeDialog(): void {
    if (!this.saving) {
      this.selected = null;
      this.draft = null;
      this.rejecting = null;
    }
  }
}
