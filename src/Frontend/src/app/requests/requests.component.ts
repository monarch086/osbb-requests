import { Component, Input, Output, EventEmitter } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgSelectComponent, NgOptionComponent } from '@ng-select/ng-select';
import { ManagedTicket } from '../api.service';
import {
  TicketFilters,
  requestTargets,
  buildingFloors,
  requestLocationLabel,
  ticketStatuses,
  ticketCategories,
  statusLabels,
  formatDate,
} from '../shared/ticket-presentation';

@Component({
  selector: 'app-requests',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './requests.component.html',
})
export class RequestsComponent {
  @Input() tickets: ManagedTicket[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() applyFilters = new EventEmitter<TicketFilters>();
  @Output() openTicket = new EventEmitter<ManagedTicket>();
  @Input() filters: TicketFilters = {
    showCompleted: false,
    status: '',
    category: '',
    search: '',
    from: '',
    to: '',
    target: '',
    floor: '',
  };
  readonly targets = requestTargets;
  readonly floors = buildingFloors;
  readonly locationLabel = requestLocationLabel;
  readonly statuses = ticketStatuses;
  readonly categories = ticketCategories;
  readonly labels = statusLabels;
  readonly date = formatDate;
  get newCount(): number {
    return this.tickets.filter((t) => t.request.status === 'New').length;
  }
  get progressCount(): number {
    return this.tickets.filter((t) => t.request.status === 'InProgress').length;
  }
  submitFilters(): void {
    this.applyFilters.emit({ ...this.filters });
  }
}
