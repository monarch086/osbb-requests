import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  NgLabelTemplateDirective,
  NgOptionComponent,
  NgOptionTemplateDirective,
  NgSelectComponent,
} from '@ng-select/ng-select';

import type { RequestAuthor, Ticket } from '../api.service';
import {
  buildingFloors,
  entranceOptions,
  floorLabel,
  formatDate,
  requestLocationLabel,
  requestTarget,
  requestTargets,
  statusLabels,
  ticketPriorities,
  ticketStatuses,
} from '../shared/ticket-presentation';

@Component({
  selector: 'app-ticket-dialog',
  imports: [
    FormsModule,
    NgSelectComponent,
    NgOptionComponent,
    NgLabelTemplateDirective,
    NgOptionTemplateDirective,
  ],
  templateUrl: './ticket-dialog.component.html',
})
export class TicketDialogComponent {
  @Input({ required: true }) draft!: Ticket;
  @Input() assignees: {
    userId: string;
    email: string;
  }[] = [];
  @Input() author: RequestAuthor | null = null;
  @Input() authorLoading = false;
  @Input() history: Record<string, string>[] = [];
  @Input() saving = false;
  @Input() error = '';
  @Output() readonly saved = new EventEmitter<Ticket>();
  @Output() readonly closed = new EventEmitter<void>();
  protected readonly targets = requestTargets;
  protected readonly floors = buildingFloors;
  protected readonly floorLabel = floorLabel;
  protected readonly locationLabel = requestLocationLabel;
  protected readonly priorities = ticketPriorities;
  protected readonly statuses = ticketStatuses;
  protected readonly labels = statusLabels;
  protected readonly historyLabels: Readonly<Partial<Record<string, string>>> = statusLabels;
  protected readonly date = formatDate;
  protected get authorName(): string {
    if (this.author?.personalDataDeleted) {
      return 'Мешканець видалив свої дані';
    }
    return (
      [this.author?.firstName, this.author?.lastName].filter(Boolean).join(' ').trim() ||
      'Мешканець'
    );
  }
  protected get entrances(): typeof entranceOptions {
    return this.draft.buildingNumber === '4Б'
      ? entranceOptions.filter((option) => option.value !== 2)
      : entranceOptions;
  }
  protected get target(): string | null {
    return requestTarget(this.draft);
  }
  protected set target(value: string | null) {
    this.draft.scope = value === 'Complex' ? 'Complex' : value ? 'Building' : null;
    this.draft.buildingNumber = value?.startsWith('Building')
      ? value.slice('Building'.length)
      : null;
    this.draft.floor = null;
    this.draft.entrance = this.draft.buildingNumber === '4Б' ? 1 : null;
  }
}
