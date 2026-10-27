import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgOptionComponent, NgSelectComponent } from '@ng-select/ng-select';

import type { RegisteredResident, Registration } from '../api.service';
import { buildingNumbers, formatDate } from '../shared/ticket-presentation';
import type { ResidentFilters } from './resident-directory';

export type ResidentView = 'pending' | 'registered';

@Component({
  selector: 'app-residents',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './residents.component.html',
})
export class ResidentsComponent {
  @Input() view: ResidentView = 'pending';
  @Input() registeredResidents: RegisteredResident[] = [];
  @Input() filters: ResidentFilters = { sort: 'registeredAt', direction: 'desc', building: '' };
  @Output() readonly filtersChange = new EventEmitter<ResidentFilters>();
  @Output() readonly viewChange = new EventEmitter<ResidentView>();
  @Input() residents: Registration[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() readonly approve = new EventEmitter<Registration>();
  @Output() readonly reject = new EventEmitter<Registration>();
  protected readonly date = formatDate;
  protected readonly buildings = buildingNumbers;
  protected changeFilters(change: Partial<ResidentFilters>): void {
    this.filtersChange.emit({ ...this.filters, ...change });
  }
}
