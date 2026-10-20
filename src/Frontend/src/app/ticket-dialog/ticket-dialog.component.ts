import { Component, Input, Output, EventEmitter } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgSelectComponent, NgOptionComponent } from '@ng-select/ng-select';
import { Ticket } from '../api.service';
import { ticketStatuses, statusLabels, formatDate } from '../shared/ticket-presentation';

@Component({
  selector: 'app-ticket-dialog',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './ticket-dialog.component.html',
})
export class TicketDialogComponent {
  @Input({ required: true }) draft!: Ticket;
  @Input() assignees: {
    userId: string;
    email: string;
  }[] = [];
  @Input() history: Record<string, string>[] = [];
  @Input() saving = false;
  @Input() error = '';
  @Output() saved = new EventEmitter<Ticket>();
  @Output() closed = new EventEmitter<void>();
  readonly statuses = ticketStatuses;
  readonly labels = statusLabels;
  readonly date = formatDate;
}
