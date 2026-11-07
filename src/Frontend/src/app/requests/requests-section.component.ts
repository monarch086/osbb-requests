import { Component, inject } from '@angular/core';

import type { ManagedTicket, RequestAuthor, Ticket } from '../api.models';
import { errorMessage } from '../error-message';
import { ExcelExportService } from '../export/excel-export.service';
import { PaginationComponent } from '../pagination/pagination.component';
import type { TicketFilters } from '../shared/ticket-presentation';
import { TicketDialogComponent } from '../ticket-dialog/ticket-dialog.component';
import { WorkspaceSection } from '../workspace/workspace-section';
import { RequestsComponent } from './requests.component';
import { RequestsList } from './requests-list';

@Component({
  selector: 'app-requests-section',
  imports: [RequestsComponent, TicketDialogComponent, PaginationComponent],
  templateUrl: './requests-section.component.html',
})
export class RequestsSectionComponent extends WorkspaceSection {
  private readonly excel = inject(ExcelExportService);
  protected override readonly lists = new RequestsList();
  protected assignees: { userId: string; email: string }[] = [];
  protected selected: ManagedTicket | null = null;
  protected draft: Ticket | null = null;
  protected history: Record<string, string>[] = [];
  protected author: RequestAuthor | null = null;
  protected authorLoading = false;
  private assigneesReady = false;
  private detailSequence = 0;
  protected override async loadItems(
    index: number,
    cursor: string | null,
    isCurrent: () => boolean,
  ): Promise<void> {
    const page = await this.api.getTickets(this.lists.appliedFilters, cursor);
    if (isCurrent()) {
      this.lists.tickets = page.items;
      this.lists.pagination.accept(index, cursor, page.cursor);
    }
  }
  protected override async load(): Promise<void> {
    if (this.loading || this.saving) {
      return;
    }
    if (!this.assigneesReady) {
      await this.initialize();
    } else {
      await super.load();
    }
  }
  private async initialize(): Promise<void> {
    const epoch = this.epoch;
    const assignees: { userId: string; email: string }[] = [];
    let cursor: string | null = null;
    this.loading = true;
    try {
      do {
        const page = await this.api.getAssignees(cursor);
        assignees.push(...page.items);
        cursor = page.cursor;
      } while (cursor && epoch === this.epoch);
      if (epoch === this.epoch) {
        this.assignees = assignees;
        this.assigneesReady = true;
        this.loading = false;
        await super.load();
      }
    } catch (error) {
      if (epoch === this.epoch) {
        this.error = errorMessage(error);
      }
    } finally {
      if (epoch === this.epoch) {
        this.loading = false;
      }
    }
  }
  protected async exportExcel(): Promise<void> {
    await this.runExport((current) => this.excel.exportTickets(this.lists.appliedFilters, current));
  }
  protected async applyRequestFilters(filters: TicketFilters): Promise<void> {
    if (this.loading || this.saving) {
      return;
    }
    this.lists.applyTicketFilters(filters);
    await this.load();
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
  protected closeDialog(): void {
    if (!this.saving) {
      this.selected = null;
      this.draft = null;
    }
  }
}
