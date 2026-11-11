import type { ManagedTicket, TicketFilters } from '../api.models';
import { WorkspaceLists } from '../workspace/workspace-lists';
import type { RequestCounts } from './request-counts';

export class RequestsList extends WorkspaceLists {
  tickets: ManagedTicket[] = [];
  counts: RequestCounts | null = null;
  filters: TicketFilters = this.defaultFilters();
  appliedFilters: TicketFilters = this.copyFilters(this.filters);

  override itemCount(): number {
    return this.tickets.length;
  }
  override resetPagination(): void {
    this.pagination.reset();
    this.tickets = [];
    this.counts = null;
  }
  applyTicketFilters(filters: TicketFilters): void {
    this.filters = this.copyFilters(filters);
    this.appliedFilters = this.copyFilters(filters);
    this.resetPagination();
  }
  private defaultFilters(): TicketFilters {
    return {
      section: 'complex',
      status: ['New', 'InProgress'],
      category: '',
      search: '',
      from: '',
      to: '',
      target: '',
      floor: '',
    };
  }
  private copyFilters(filters: TicketFilters): TicketFilters {
    return { ...filters, status: [...filters.status] };
  }
}
