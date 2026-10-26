import { Component, Input, Output, EventEmitter } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgSelectComponent, NgOptionComponent } from '@ng-select/ng-select';
import { Registration, RegisteredResident } from '../api.service';
import { buildingNumbers, formatDate } from '../shared/ticket-presentation';
import { ResidentFilters } from './resident-directory';

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
  @Output() filtersChange = new EventEmitter<ResidentFilters>();
  @Output() viewChange = new EventEmitter<ResidentView>();
  @Input() residents: Registration[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() approve = new EventEmitter<Registration>();
  @Output() reject = new EventEmitter<Registration>();
  readonly date = formatDate;
  readonly buildings = buildingNumbers;
  changeFilters(change: Partial<ResidentFilters>): void {
    this.filtersChange.emit({ ...this.filters, ...change });
  }
}
