import { Component, Input, Output, EventEmitter } from '@angular/core';
import { Registration, RegisteredResident } from '../api.service';
import { formatDate } from '../shared/ticket-presentation';

export type ResidentView = 'pending' | 'registered';

@Component({
  selector: 'app-residents',
  imports: [],
  templateUrl: './residents.component.html',
})
export class ResidentsComponent {
  @Input() view: ResidentView = 'pending';
  @Input() registeredResidents: RegisteredResident[] = [];
  @Output() viewChange = new EventEmitter<ResidentView>();
  @Input() residents: Registration[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() approve = new EventEmitter<Registration>();
  @Output() reject = new EventEmitter<Registration>();
  readonly date = formatDate;
}
