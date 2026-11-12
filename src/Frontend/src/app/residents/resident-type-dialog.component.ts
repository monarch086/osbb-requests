import type { OnInit } from '@angular/core';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgOptionComponent, NgSelectComponent } from '@ng-select/ng-select';

import type {
  RegisteredResident,
  Registration,
  ResidentDataChange,
  ResidentType,
} from '../api.models';

@Component({
  selector: 'app-resident-type-dialog',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './resident-type-dialog.component.html',
})
export class ResidentTypeDialogComponent implements OnInit {
  @Input({ required: true }) registration!: Registration | RegisteredResident;
  @Input() saving = false;
  @Input() error = '';
  @Output() readonly saved = new EventEmitter<ResidentDataChange>();
  @Output() readonly closed = new EventEmitter<void>();
  protected type: ResidentType = 'FamilyMember';
  protected firstName = '';
  protected lastName = '';
  protected get valid(): boolean {
    const validName = (name: string): boolean =>
      /^(?=.*\p{L})[\p{L}\p{Mn} '’ʼ-]{1,80}$/u.test(name.trim());
    return (
      validName(this.firstName) &&
      validName(this.lastName) &&
      (this.firstName.trim() !== (this.registration.firstName ?? '') ||
        this.lastName.trim() !== (this.registration.lastName ?? '') ||
        this.type !== (this.registration.residentType ?? 'FamilyMember'))
    );
  }
  protected save(): void {
    if (this.valid && !this.saving) {
      this.saved.emit({
        firstName: this.firstName.trim(),
        lastName: this.lastName.trim(),
        residentType: this.type,
      });
    }
  }
  ngOnInit(): void {
    this.type = this.registration.residentType ?? 'FamilyMember';
    this.firstName = this.registration.firstName ?? '';
    this.lastName = this.registration.lastName ?? '';
  }
}
