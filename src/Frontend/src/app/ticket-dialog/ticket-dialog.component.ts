import { Component, Input, Output, EventEmitter } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgSelectComponent, NgOptionComponent } from '@ng-select/ng-select';
import { Ticket } from '../api.service';
import {
  ticketStatuses,
  statusLabels,
  formatDate,
  requestTargets,
  buildingFloors,
  entranceOptions,
  floorLabel,
  requestTarget,
  requestLocationLabel,
} from '../shared/ticket-presentation';

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
  readonly targets = requestTargets;
  get entrances(): typeof entranceOptions {
    return this.draft.buildingNumber === '4Б'
      ? entranceOptions.filter((option) => option.value !== 2)
      : entranceOptions;
  }
  readonly floors = buildingFloors;
  readonly floorLabel = floorLabel;
  readonly locationLabel = requestLocationLabel;
  get target(): string | null {
    return requestTarget(this.draft);
  }
  set target(value: string | null) {
    this.draft.scope = value === 'Complex' ? 'Complex' : value ? 'Building' : null;
    this.draft.buildingNumber = value?.startsWith('Building')
      ? value.slice('Building'.length)
      : null;
    this.draft.floor = null;
    this.draft.entrance = this.draft.buildingNumber === '4Б' ? 1 : null;
  }
  readonly statuses = ticketStatuses;
  readonly labels = statusLabels;
  readonly date = formatDate;
}
