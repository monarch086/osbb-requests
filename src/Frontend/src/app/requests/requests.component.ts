import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgOptionComponent, NgSelectComponent } from '@ng-select/ng-select';

import type { ManagedTicket, TicketStatus } from '../api.models';
import type { TicketFilters } from '../shared/ticket-presentation';
import {
  buildingFloors,
  floorLabel,
  formatDate,
  requestLocationLabel,
  requestTargets,
  statusLabels,
  ticketCategories,
  ticketPriorities,
  ticketStatuses,
} from '../shared/ticket-presentation';

@Component({
  selector: 'app-requests',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './requests.component.html',
  styleUrl: './requests.component.scss',
})
export class RequestsComponent {
  @Input() tickets: ManagedTicket[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() readonly applyFilters = new EventEmitter<TicketFilters>();
  @Output() readonly openTicket = new EventEmitter<ManagedTicket>();
  @Input() filters: TicketFilters = {
    status: ['New', 'InProgress'],
    category: '',
    search: '',
    from: '',
    to: '',
    target: '',
    floor: '',
  };
  protected readonly priorities = ticketPriorities;
  protected readonly targets = requestTargets;
  protected readonly floors = buildingFloors;
  protected readonly floorLabel = floorLabel;
  protected readonly locationLabel = requestLocationLabel;
  protected readonly statuses = ticketStatuses;
  protected readonly categories = ticketCategories;
  protected readonly labels = statusLabels;
  protected readonly badgeClasses: Record<TicketStatus, string> = {
    New: 'text-bg-warning',
    InProgress: 'text-bg-primary status-in-progress',
    AwaitingClarification: 'text-bg-secondary',
    Completed: 'text-bg-success',
    Rejected: 'text-bg-danger',
  };
  protected readonly date = formatDate;
  protected get newCount(): number {
    return this.tickets.filter((t) => t.request.status === 'New').length;
  }
  protected get progressCount(): number {
    return this.tickets.filter((t) => t.request.status === 'InProgress').length;
  }
  protected submitFilters(): void {
    this.applyFilters.emit({ ...this.filters, status: [...this.filters.status] });
  }
}
