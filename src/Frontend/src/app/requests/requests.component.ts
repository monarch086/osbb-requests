import { Component, Input, Output, EventEmitter } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ManagedTicket } from '../api.service';
import { TicketFilters, ticketStatuses, ticketCategories, statusLabels, formatDate } from '../shared/ticket-presentation';

@Component({
  selector: 'app-requests',
  imports: [FormsModule],
  templateUrl: './requests.component.html'
})
export class RequestsComponent {
  @Input() tickets: ManagedTicket[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() applyFilters = new EventEmitter<TicketFilters>();
  @Output() openTicket = new EventEmitter<ManagedTicket>();
  @Input() filters: TicketFilters = { status: '', category: '', search: '', from: '', to: '' };
  readonly statuses = ticketStatuses;
  readonly categories = ticketCategories;
  readonly labels = statusLabels;
  readonly date = formatDate;
  get newCount(): number { return this.tickets.filter(t => t.request.status === 'New').length; }
  get progressCount(): number { return this.tickets.filter(t => t.request.status === 'InProgress').length; }
  submitFilters(): void { this.applyFilters.emit({ ...this.filters }); }
}
