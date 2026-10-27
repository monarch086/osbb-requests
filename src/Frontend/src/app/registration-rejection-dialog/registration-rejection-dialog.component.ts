import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

import type { Registration } from '../api.service';

@Component({
  selector: 'app-registration-rejection-dialog',
  imports: [FormsModule],
  templateUrl: './registration-rejection-dialog.component.html',
})
export class RegistrationRejectionDialogComponent {
  @Input({ required: true }) registration!: Registration;
  @Input() saving = false;
  @Input() error = '';
  @Output() readonly rejected = new EventEmitter<string>();
  @Output() readonly closed = new EventEmitter<void>();
  protected reason = '';
}
