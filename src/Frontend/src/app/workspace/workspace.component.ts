import { Component, OnDestroy, OnInit, Output, EventEmitter, inject } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AuthService } from '../auth.service';
import {
  ApiService,
  Admin,
  Registration,
  RegisteredResident,
  ManagedTicket,
  Ticket,
  Page,
} from '../api.service';
import { PaginationComponent } from '../pagination/pagination.component';
import { TicketFilters } from '../shared/ticket-presentation';
import { errorMessage } from '../error-message';
import { RequestsComponent } from '../requests/requests.component';
import { ResidentsComponent, ResidentView } from '../residents/residents.component';
import { AdministratorsComponent } from '../administrators/administrators.component';
import { RegistrationRejectionDialogComponent } from '../registration-rejection-dialog/registration-rejection-dialog.component';
import { TicketDialogComponent } from '../ticket-dialog/ticket-dialog.component';

@Component({
  selector: 'app-workspace',
  imports: [
    RouterLink,
    RequestsComponent,
    PaginationComponent,
    ResidentsComponent,
    AdministratorsComponent,
    RegistrationRejectionDialogComponent,
    TicketDialogComponent,
  ],
  templateUrl: './workspace.component.html',
})
export class WorkspaceComponent implements OnInit, OnDestroy {
  @Output() savingChange = new EventEmitter<boolean>();
  private route = inject(ActivatedRoute);
  private router = inject(Router);
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
  registeredResidents: RegisteredResident[] = [];
  residentView: ResidentView = 'pending';
  admins: Admin[] = [];
  assignees: {
    userId: string;
    email: string;
  }[] = [];
  cursor: string | null = null;
  pageIndex = 0;
  private pageCursors: (string | null)[] = [null];
  selected: ManagedTicket | null = null;
  draft: Ticket | null = null;
  history: Record<string, string>[] = [];
  rejecting: Registration | null = null;
  filters: TicketFilters = {
    status: ['New', 'InProgress'],
    category: '',
    search: '',
    from: '',
    to: '',
    target: '',
    floor: '',
  };
  private appliedFilters: TicketFilters = { ...this.filters, status: [...this.filters.status] };
  private loadSequence = 0;
  get availablePages(): number {
    return this.pageCursors.length;
  }
  get pageItemCount(): number {
    if (this.tab === 'requests') return this.tickets.length;
    if (this.tab === 'admins') return this.admins.length;
    return this.residentView === 'pending'
      ? this.residents.length
      : this.registeredResidents.length;
  }
  private resetPagination(): void {
    this.pageIndex = 0;
    this.pageCursors = [null];
    this.cursor = null;
    this.tickets = [];
    this.residents = [];
    this.registeredResidents = [];
    this.admins = [];
  }
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
    this.tab = this.route.snapshot.data['tab'] as typeof this.tab;
    this.filters = {
      status: ['New', 'InProgress'],
      category: '',
      search: '',
      from: '',
      to: '',
      target: '',
      floor: '',
    };
    this.appliedFilters = { ...this.filters, status: [...this.filters.status] };
    this.resetPagination();
    this.me = null;
    this.residentView = 'pending';
    this.assignees = [];
    this.selected = null;
    this.draft = null;
    this.rejecting = null;
    this.history = [];
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
      if (!user.isSuperAdmin && this.tab === 'admins') {
        await this.router.navigateByUrl('/requests', { replaceUrl: true });
        return;
      }
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
        await this.loadPage(0, null);
      }
    } catch (e) {
      if (epoch === this.epoch) this.error = errorMessage(e);
    } finally {
      if (epoch === this.epoch) this.loading = false;
    }
  }
  async switchResidentView(view: ResidentView): Promise<void> {
    if (this.saving || this.loading || this.tab !== 'residents' || view === this.residentView)
      return;
    this.residentView = view;
    this.resetPagination();
    this.rejecting = null;
    this.notice = '';
    await this.load();
  }
  async applyRequestFilters(filters: TicketFilters): Promise<void> {
    if (this.loading || this.saving) return;
    this.filters = { ...filters, status: [...filters.status] };
    this.appliedFilters = { ...filters, status: [...filters.status] };
    this.resetPagination();
    await this.load();
  }
  async load(): Promise<void> {
    if (this.loading || this.saving) return;
    await this.loadPage(this.pageIndex, this.pageCursors[this.pageIndex] ?? null);
  }
  async previousPage(): Promise<void> {
    await this.goToPage(this.pageIndex);
  }
  async nextPage(): Promise<void> {
    if (!this.cursor) return;
    await this.goToPage(this.pageIndex + 2);
  }
  async goToPage(number: number): Promise<void> {
    if (
      this.loading ||
      this.saving ||
      !Number.isInteger(number) ||
      number < 1 ||
      number > this.availablePages ||
      number === this.pageIndex + 1
    )
      return;
    const index = number - 1;
    const start = this.pageCursors[index];
    if (start === undefined || (index > 0 && start === null)) return;
    await this.loadPage(index, start);
  }
  private async loadPage(index: number, startCursor: string | null): Promise<void> {
    if (!this.me?.isActive || this.saving) return;
    const sequence = ++this.loadSequence;
    const epoch = this.epoch;
    const tab = this.tab;
    const residentView = this.residentView;
    this.loading = true;
    this.error = '';
    const params = new URLSearchParams();
    if (startCursor) params.set('cursor', startCursor);
    if (tab === 'requests') {
      const { status, ...filters } = this.appliedFilters;
      if (status.length) params.set('status', status.join(','));
      for (const [key, value] of Object.entries(filters)) {
        if (value) params.set(key, value);
      }
    }
    const path =
      tab === 'requests'
        ? '/requests'
        : tab === 'residents'
          ? residentView === 'pending'
            ? '/registrations'
            : '/residents'
          : '/administrators';
    try {
      const page = await this.api.request<
        Page<ManagedTicket | Registration | RegisteredResident | Admin>
      >(path + '?' + params.toString());
      if (
        epoch !== this.epoch ||
        sequence !== this.loadSequence ||
        tab !== this.tab ||
        (tab === 'residents' && residentView !== this.residentView)
      )
        return;
      if (tab === 'requests') this.tickets = page.items as ManagedTicket[];
      if (tab === 'residents' && residentView === 'pending')
        this.residents = page.items as Registration[];
      if (tab === 'residents' && residentView === 'registered')
        this.registeredResidents = page.items as RegisteredResident[];
      if (tab === 'admins') this.admins = page.items as Admin[];
      this.pageIndex = index;
      this.pageCursors[index] = startCursor;
      if (page.cursor) {
        if (this.pageCursors[index + 1] !== page.cursor)
          this.pageCursors = this.pageCursors.slice(0, index + 1);
        this.pageCursors[index + 1] = page.cursor;
      } else this.pageCursors = this.pageCursors.slice(0, index + 1);
      this.cursor = page.cursor;
    } catch (e) {
      if (epoch === this.epoch && sequence === this.loadSequence) this.error = errorMessage(e);
    } finally {
      if (epoch === this.epoch && sequence === this.loadSequence) this.loading = false;
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
    if (this.saving || this.loading) return;
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
        scope: draft.scope,
        buildingNumber: draft.buildingNumber,
        floor: draft.floor,
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
