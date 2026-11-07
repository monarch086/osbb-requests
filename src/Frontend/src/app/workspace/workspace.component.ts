import type { OnDestroy, OnInit } from '@angular/core';
import { Component, EventEmitter, inject, Output } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';

import { AdministratorsComponent } from '../administrators/administrators.component';
import type {
  Admin,
  ManagedTicket,
  MessageChange,
  NotificationSettings,
  Registration,
  RequestAuthor,
  Ticket,
} from '../api.models';
import { ApiService } from '../api.service';
import { AuthService } from '../auth.service';
import { errorMessage } from '../error-message';
import { ExcelExportService } from '../export/excel-export.service';
import { MessagesComponent } from '../messages/messages.component';
import { PaginationComponent } from '../pagination/pagination.component';
import { RegistrationRejectionDialogComponent } from '../registration-rejection-dialog/registration-rejection-dialog.component';
import { RequestsComponent } from '../requests/requests.component';
import type { ResidentFilters, ResidentView } from '../residents/resident-directory';
import { ResidentsComponent } from '../residents/residents.component';
import type { TicketFilters } from '../shared/ticket-presentation';
import { TicketDialogComponent } from '../ticket-dialog/ticket-dialog.component';
import { WorkspaceLists } from './workspace-lists';
import type { WorkspaceTab } from './workspace-page.models';
import { WorkspacePageService } from './workspace-page.service';

@Component({
  selector: 'app-workspace',
  imports: [
    RouterLink,
    MessagesComponent,
    RequestsComponent,
    PaginationComponent,
    ResidentsComponent,
    AdministratorsComponent,
    RegistrationRejectionDialogComponent,
    TicketDialogComponent,
  ],
  templateUrl: './workspace.component.html',
  providers: [WorkspacePageService],
})
export class WorkspaceComponent implements OnInit, OnDestroy {
  @Output() readonly savingChange = new EventEmitter<boolean>();
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly excel = inject(ExcelExportService);
  private readonly pageService = inject(WorkspacePageService);
  protected readonly lists = new WorkspaceLists();
  protected me: Admin | null = null;
  protected loading = false;
  saving = false;
  protected error = '';
  protected notice = '';
  protected tab: WorkspaceTab = 'requests';
  protected sentMessageId: string | null = null;
  protected assignees: {
    userId: string;
    email: string;
  }[] = [];
  protected selected: ManagedTicket | null = null;
  protected draft: Ticket | null = null;
  protected history: Record<string, string>[] = [];
  protected author: RequestAuthor | null = null;
  protected authorLoading = false;
  protected rejecting: Registration | null = null;
  private loadSequence = 0;
  private detailSequence = 0;
  private readonly subscription = new Subscription();
  private epoch = 0;
  private initializing = false;
  ngOnInit(): void {
    this.subscription.add(
      this.route.paramMap.subscribe((params) => {
        const value = params.get('tab');
        const tab = value === 'administrators' ? 'admins' : value;
        if (tab !== 'requests' && tab !== 'residents' && tab !== 'messages' && tab !== 'admins') {
          return;
        }
        if (tab === this.tab) {
          return;
        }
        this.tab = tab;
        this.loadSequence++;
        this.detailSequence++;
        this.resetTab();
        if (this.me?.isActive && !this.initializing) {
          if (!this.me.isSuperAdmin && (tab === 'admins' || tab === 'messages')) {
            void this.router.navigateByUrl('/requests', { replaceUrl: true });
          } else {
            void this.loadPage(0, null);
          }
        }
      }),
    );
    this.subscription.add(
      this.auth.user$.subscribe((user) => {
        this.epoch++;
        this.clear();
        if (user) {
          void this.workspace();
        }
      }),
    );
  }
  ngOnDestroy(): void {
    this.epoch++;
    this.subscription.unsubscribe();
    this.clear();
  }
  private clear(): void {
    this.resetTab();
    this.me = null;
    this.assignees = [];
    this.initializing = false;
    this.loading = false;
  }
  private resetTab(): void {
    this.lists.resetTab();
    this.selected = null;
    this.draft = null;
    this.rejecting = null;
    this.history = [];
    this.author = null;
    this.authorLoading = false;
    this.notice = '';
    this.sentMessageId = null;
  }
  protected async workspace(): Promise<void> {
    const epoch = this.epoch;
    this.initializing = true;
    this.loading = true;
    this.error = '';
    let pageStarted = false;
    try {
      const user = await this.api.getCurrentAdmin();
      if (epoch !== this.epoch) {
        return;
      }
      this.me = user;
      if (!user.isSuperAdmin && (this.tab === 'admins' || this.tab === 'messages')) {
        await this.router.navigateByUrl('/requests', { replaceUrl: true });
        if (epoch !== this.epoch) {
          return;
        }
      }
      if (user.isActive) {
        const assignees: {
          userId: string;
          email: string;
        }[] = [];
        let cursor: string | null = null;
        do {
          const page = await this.api.getAssignees(cursor);
          assignees.push(...page.items);
          cursor = page.cursor;
        } while (cursor && epoch === this.epoch);
        if (epoch !== this.epoch) {
          return;
        }
        this.assignees = assignees;
        this.initializing = false;
        pageStarted = true;
        await this.loadPage(0, null);
      }
    } catch (e) {
      if (epoch === this.epoch) {
        this.error = errorMessage(e);
      }
    } finally {
      if (epoch === this.epoch) {
        this.initializing = false;
        if (!pageStarted) {
          this.loading = false;
        }
      }
    }
  }
  protected async switchResidentView(view: ResidentView): Promise<void> {
    if (
      this.saving ||
      this.loading ||
      this.tab !== 'residents' ||
      view === this.lists.residentView
    ) {
      return;
    }
    this.lists.residentView = view;
    this.lists.resetPagination();
    this.rejecting = null;
    this.notice = '';
    await this.load();
  }
  protected async exportExcel(): Promise<void> {
    if (
      !this.me?.isActive ||
      this.loading ||
      this.saving ||
      (this.tab !== 'requests' && this.tab !== 'residents')
    ) {
      return;
    }
    const epoch = this.epoch;
    const sequence = ++this.loadSequence;
    const tab = this.tab;
    const view = this.lists.residentView;
    const isCurrent = (): boolean =>
      epoch === this.epoch &&
      sequence === this.loadSequence &&
      tab === this.tab &&
      (tab !== 'residents' || view === this.lists.residentView);
    this.loading = true;
    this.error = '';
    try {
      if (tab === 'requests') {
        await this.excel.exportTickets(this.lists.appliedFilters, isCurrent);
      } else {
        await this.excel.exportResidents(view, this.lists.residentDirectory, isCurrent);
      }
    } catch (error) {
      if (isCurrent()) {
        this.error = errorMessage(error);
      }
    } finally {
      if (isCurrent()) {
        this.loading = false;
      }
    }
  }
  protected async applyRequestFilters(filters: TicketFilters): Promise<void> {
    if (this.loading || this.saving) {
      return;
    }
    this.lists.applyTicketFilters(filters);
    await this.load();
  }
  protected applyResidentFilters(filters: ResidentFilters): void {
    if (this.loading || this.saving || !this.lists.isRegisteredView(this.tab)) {
      return;
    }
    this.lists.residentDirectory.applyFilters(filters);
    this.lists.showResidentPage(0);
  }
  protected async load(): Promise<void> {
    if (this.loading || this.saving) {
      return;
    }
    await this.loadPage(
      this.lists.pagination.index,
      this.lists.pagination.start(this.lists.pagination.index) ?? null,
    );
  }
  protected async previousPage(): Promise<void> {
    await this.goToPage(this.lists.pagination.index);
  }
  protected async nextPage(): Promise<void> {
    if (!this.lists.hasNext(this.tab)) {
      return;
    }
    await this.goToPage(this.lists.pagination.index + 2);
  }
  protected async goToPage(number: number): Promise<void> {
    if (
      this.loading ||
      this.saving ||
      !Number.isInteger(number) ||
      number < 1 ||
      number > this.lists.availablePages(this.tab) ||
      number === this.lists.pagination.index + 1
    ) {
      return;
    }
    const index = number - 1;
    if (this.lists.isRegisteredView(this.tab)) {
      this.lists.showResidentPage(index);
      return;
    }
    const start = this.lists.pagination.start(index);
    if (start === undefined || (index > 0 && start === null)) {
      return;
    }
    await this.loadPage(index, start);
  }
  private async loadPage(index: number, startCursor: string | null): Promise<void> {
    if (!this.me?.isActive || this.saving) {
      return;
    }
    const sequence = ++this.loadSequence;
    const epoch = this.epoch;
    const tab = this.tab;
    const residentView = this.lists.residentView;
    this.loading = true;
    this.error = '';
    try {
      if (tab === 'residents' && residentView === 'registered') {
        const residents = await this.pageService.residents(
          () => epoch === this.epoch && sequence === this.loadSequence,
        );
        if (!residents) {
          return;
        }
        this.lists.residentDirectory.setItems(residents);
        this.lists.showResidentPage(index);
        return;
      }
      const result = await this.pageService.load(tab, this.lists.appliedFilters, startCursor);
      if (
        epoch !== this.epoch ||
        sequence !== this.loadSequence ||
        tab !== this.tab ||
        (tab === 'residents' && residentView !== this.lists.residentView)
      ) {
        return;
      }
      switch (result.kind) {
        case 'requests':
          this.lists.tickets = result.page.items;
          break;
        case 'residents':
          this.lists.residents = result.page.items;
          break;
        case 'messages':
          this.lists.messages = result.page.items;
          break;
        case 'admins':
          this.lists.admins = result.page.items;
          break;
      }
      const page = result.page;
      this.lists.pagination.accept(index, startCursor, page.cursor);
    } catch (e) {
      if (epoch === this.epoch && sequence === this.loadSequence) {
        this.error = errorMessage(e);
      }
    } finally {
      if (epoch === this.epoch && sequence === this.loadSequence) {
        this.loading = false;
      }
    }
  }
  protected async sendMessage(change: MessageChange): Promise<void> {
    if (!this.me?.isSuperAdmin || !this.me.isActive || this.loading || this.saving) {
      return;
    }
    const epoch = this.epoch;
    const writeState = { sent: false };
    await this.write(async () => {
      const result = await this.api.sendMessage(change);
      if (epoch !== this.epoch) {
        return;
      }
      this.sentMessageId = result.id;
      this.notice = 'Повідомлення передано на розсилку мешканцям у Telegram.';
      writeState.sent = true;
    });
    if (writeState.sent && epoch === this.epoch) {
      this.lists.resetPagination();
      await this.load();
    }
  }
  protected async saveNotificationSettings(
    admin: Admin,
    settings: NotificationSettings,
  ): Promise<void> {
    if (!this.me?.isSuperAdmin) {
      return;
    }
    await this.write(async () => {
      await this.api.updateNotificationSettings(admin, settings);
      Object.assign(admin, settings, { version: admin.version + 1 });
      if (this.me?.userId === admin.userId) {
        Object.assign(this.me, settings, { version: admin.version });
      }
      this.notice = 'Налаштування сповіщень збережено.';
    });
  }
  protected async setAccess(user: Admin): Promise<void> {
    if (!this.me?.isSuperAdmin || user.isSuperAdmin) {
      return;
    }
    await this.write(async () => {
      await this.api.updateAdminAccess(user);
      user.isActive = !user.isActive;
      user.version++;
      this.notice = 'Доступ адміністратора оновлено.';
    });
  }
  protected async decide(
    registration: Registration,
    approved: boolean,
    reason = '',
  ): Promise<void> {
    if (!approved && !reason.trim()) {
      this.error = 'Вкажіть причину відхилення.';
      return;
    }
    await this.write(async () => {
      await this.api.decideRegistration(registration, approved, approved ? null : reason.trim());
      this.lists.residents = this.lists.residents.filter((r) => r.id !== registration.id);
      this.rejecting = null;
      this.notice = approved ? 'Мешканцю надано доступ.' : 'Реєстрацію відхилено.';
    });
  }
  protected async open(ticket: ManagedTicket): Promise<void> {
    if (this.saving || this.loading) {
      return;
    }
    this.selected = ticket;
    this.draft = { ...ticket.request, entrance: ticket.request.entrance ?? null };
    this.history = [];
    this.error = '';
    this.author = null;
    this.authorLoading = true;
    const sequence = ++this.detailSequence;
    const epoch = this.epoch;
    const [detail, history] = await Promise.allSettled([
      this.api.getTicket(ticket),
      this.api.getTicketHistory(ticket),
    ]);
    if (epoch !== this.epoch || sequence !== this.detailSequence || this.selected !== ticket) {
      return;
    }
    this.authorLoading = false;
    if (detail.status === 'fulfilled') {
      this.author = detail.value.author ?? null;
    } else {
      this.error = errorMessage(detail.reason);
    }
    if (history.status === 'fulfilled') {
      this.history = history.value.items;
    } else {
      this.error = errorMessage(history.reason);
    }
  }
  protected async saveTicket(draft: Ticket): Promise<void> {
    const selected = this.selected;
    if (!selected) {
      return;
    }
    const priorityChanged = selected.request.priority !== draft.priority;
    const epoch = this.epoch;
    const writeState = { saved: false };
    await this.write(async () => {
      await this.api.updateTicket(selected, draft);
      selected.request = { ...draft };
      selected.version++;
      this.selected = null;
      this.draft = null;
      this.notice = 'Зміни заявки збережено.';
      writeState.saved = true;
    });
    if (writeState.saved && priorityChanged && epoch === this.epoch) {
      this.lists.resetPagination();
      await this.load();
    }
  }
  private async write(action: () => Promise<void>): Promise<void> {
    if (this.saving) {
      return;
    }
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
  protected closeDialog(): void {
    if (!this.saving) {
      this.selected = null;
      this.draft = null;
      this.rejecting = null;
    }
  }
}
