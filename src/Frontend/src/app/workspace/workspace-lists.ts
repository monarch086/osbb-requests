import type {
  Admin,
  BroadcastMessage,
  ManagedTicket,
  RegisteredResident,
  Registration,
  TicketFilters,
} from '../api.models';
import type { ResidentView } from '../residents/resident-directory';
import { ResidentDirectory } from '../residents/resident-directory';
import type { WorkspaceTab } from './workspace-page.models';
import { WorkspacePagination } from './workspace-pagination';

export class WorkspaceLists {
  readonly pagination = new WorkspacePagination();
  messages: BroadcastMessage[] = [];
  tickets: ManagedTicket[] = [];
  residents: Registration[] = [];
  registeredResidents: RegisteredResident[] = [];
  residentView: ResidentView = 'registered';
  residentDirectory = new ResidentDirectory();
  admins: Admin[] = [];
  filters: TicketFilters = this.defaultFilters();
  appliedFilters: TicketFilters = this.copyFilters(this.filters);

  resetTab(): void {
    this.filters = this.defaultFilters();
    this.appliedFilters = this.copyFilters(this.filters);
    this.resetPagination();
    this.residentView = 'registered';
    this.residentDirectory = new ResidentDirectory();
  }

  resetPagination(): void {
    this.pagination.reset();
    this.messages = [];
    this.tickets = [];
    this.residents = [];
    this.registeredResidents = [];
    this.residentDirectory.setItems([]);
    this.admins = [];
  }

  applyTicketFilters(filters: TicketFilters): void {
    this.filters = this.copyFilters(filters);
    this.appliedFilters = this.copyFilters(filters);
    this.resetPagination();
  }

  isRegisteredView(tab: WorkspaceTab): boolean {
    return tab === 'residents' && this.residentView === 'registered';
  }

  availablePages(tab: WorkspaceTab): number {
    return this.isRegisteredView(tab)
      ? this.residentDirectory.pageCount
      : this.pagination.availablePages;
  }

  hasNext(tab: WorkspaceTab): boolean {
    return this.isRegisteredView(tab)
      ? this.pagination.index + 1 < this.availablePages(tab)
      : !!this.pagination.cursor;
  }

  itemCount(tab: WorkspaceTab): number {
    switch (tab) {
      case 'requests':
        return this.tickets.length;
      case 'messages':
        return this.messages.length;
      case 'admins':
        return this.admins.length;
      case 'residents':
        return this.residentView === 'pending'
          ? this.residents.length
          : this.registeredResidents.length;
    }
  }

  showResidentPage(index: number): void {
    this.pagination.index = Math.min(index, this.residentDirectory.pageCount - 1);
    this.registeredResidents = this.residentDirectory.page(this.pagination.index);
  }

  private defaultFilters(): TicketFilters {
    return {
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
