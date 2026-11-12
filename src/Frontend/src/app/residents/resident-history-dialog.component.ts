import { Component, EventEmitter, Input, Output } from '@angular/core';

import type { RegisteredResident, Registration, ResidentTypeHistory } from '../api.models';
import { formatDate } from '../shared/ticket-presentation';
import { residentTypeLabel } from './resident-type';

@Component({
  selector: 'app-resident-history-dialog',
  templateUrl: './resident-history-dialog.component.html',
})
export class ResidentHistoryDialogComponent {
  @Input({ required: true }) resident!: Registration | RegisteredResident;
  @Input() history: ResidentTypeHistory[] = [];
  @Input() loading = false;
  @Input() error = '';
  @Output() readonly closed = new EventEmitter<void>();
  protected readonly date = formatDate;
  protected readonly typeLabel = residentTypeLabel;
}
