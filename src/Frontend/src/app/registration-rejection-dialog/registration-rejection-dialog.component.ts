import { Component, Input, Output, EventEmitter } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Registration } from '../api.service';

@Component({
  selector: 'app-registration-rejection-dialog',
  imports: [FormsModule],
  templateUrl: './registration-rejection-dialog.component.html'
})
export class RegistrationRejectionDialogComponent {
  @Input({ required: true }) registration!: Registration;
  @Input() saving = false;
  @Input() error = '';
  @Output() rejected = new EventEmitter<string>();
  @Output() closed = new EventEmitter<void>();
  reason = '';
}
