import type { OnInit } from '@angular/core';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgOptionComponent, NgSelectComponent } from '@ng-select/ng-select';

import type { Registration, ResidentType } from '../api.models';

@Component({
  selector: 'app-resident-type-dialog',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './resident-type-dialog.component.html',
})
export class ResidentTypeDialogComponent implements OnInit {
  @Input({ required: true }) registration!: Registration;
  @Input() saving = false;
  @Input() error = '';
  @Output() readonly saved = new EventEmitter<ResidentType>();
  @Output() readonly closed = new EventEmitter<void>();
  protected type: ResidentType = 'FamilyMember';
  ngOnInit(): void {
    this.type = this.registration.residentType ?? 'FamilyMember';
  }
}
