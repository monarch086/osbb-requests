import { Component, Input, Output, EventEmitter } from '@angular/core';
import { Admin } from '../api.service';

@Component({
  selector: 'app-administrators',
  imports: [],
  templateUrl: './administrators.component.html',
})
export class AdministratorsComponent {
  @Input() admins: Admin[] = [];
  @Input() saving = false;
  @Output() toggleAccess = new EventEmitter<Admin>();
}
