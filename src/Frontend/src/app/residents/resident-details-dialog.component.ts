import type { AfterViewInit, ElementRef } from '@angular/core';
import { Component, EventEmitter, Input, Output, ViewChild } from '@angular/core';

import type {
  PagePermissions,
  RegisteredResident,
  Registration,
  RegistrationApproval,
} from '../api.models';
import { formatDate } from '../shared/ticket-presentation';
import { residentTypeLabel } from './resident-type';

@Component({
  selector: 'app-resident-details-dialog',
  templateUrl: './resident-details-dialog.component.html',
})
export class ResidentDetailsDialogComponent implements AfterViewInit {
  @Input() pending: Registration | null = null;
  @Input() resident: RegisteredResident | null = null;
  @Input({ required: true }) permissions!: PagePermissions;
  @Input() saving = false;
  @Input() error = '';
  @Input() approval: RegistrationApproval | null = null;
  @Input() approvalLoading = false;
  @Input() approvalError = false;
  @Output() readonly closed = new EventEmitter<void>();
  @Output() readonly edit = new EventEmitter<Registration | RegisteredResident>();
  @Output() readonly history = new EventEmitter<Registration | RegisteredResident>();
  @Output() readonly approve = new EventEmitter<Registration>();
  @Output() readonly reject = new EventEmitter<Registration>();
  @ViewChild('closeButton') private readonly closeButton?: ElementRef<HTMLButtonElement>;
  protected readonly typeLabel = residentTypeLabel;
  protected readonly date = formatDate;

  ngAfterViewInit(): void {
    this.closeButton?.nativeElement.focus();
  }
}
