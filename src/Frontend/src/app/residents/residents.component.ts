import { Component, Input, Output, EventEmitter } from '@angular/core';
import { Registration } from '../api.service';
import { formatDate } from '../shared/ticket-presentation';

@Component({
  selector: 'app-residents',
  imports: [],
  templateUrl: './residents.component.html',
})
export class ResidentsComponent {
  @Input() residents: Registration[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() approve = new EventEmitter<Registration>();
  @Output() reject = new EventEmitter<Registration>();
  readonly date = formatDate;
}
