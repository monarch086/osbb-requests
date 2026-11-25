import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgOptionComponent, NgSelectComponent } from '@ng-select/ng-select';

import type {
  PagePermissions,
  Registration,
  ResidentCounts,
  ResidentKey,
  ResidentKeyEntry,
  ResidentListRow,
} from '../api.models';
import { buildingNumbers } from '../shared/ticket-presentation';
import type { ResidentFilters, ResidentView } from './resident-directory';
import { residentTypeLabel } from './resident-type';

@Component({
  selector: 'app-residents',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './residents.component.html',
  styleUrl: './residents.component.scss',
})
export class ResidentsComponent {
  @Input() view: ResidentView = 'registered';
  @Input() pendingCount = 0;
  @Input() apartmentCount = 0;
  @Input() counts: ResidentCounts | null = null;
  @Input({ required: true }) permissions!: PagePermissions;
  @Input() pendingBuilding = '';
  @Output() readonly pendingBuildingChange = new EventEmitter<string>();
  protected readonly typeLabel = residentTypeLabel;
  @Input() registeredResidents: ResidentListRow[] = [];
  @Input() keyResidents: ResidentKeyEntry[] = [];
  @Output() readonly editKey = new EventEmitter<ResidentKey>();
  @Output() readonly removeKey = new EventEmitter<ResidentKey>();
  @Input() filters: ResidentFilters = {
    sort: 'registeredAt',
    direction: 'desc',
    building: '',
    residentType: '',
  };
  @Output() readonly filtersChange = new EventEmitter<ResidentFilters>();
  @Output() readonly viewChange = new EventEmitter<ResidentView>();
  @Input() residents: Registration[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Output() readonly selectPending = new EventEmitter<Registration>();
  @Output() readonly selectRegistered = new EventEmitter<ResidentListRow>();
  protected readonly buildings = buildingNumbers;
  protected residentAddress(
    buildingNumber?: string | null,
    apartmentNumber?: string | null,
  ): string {
    const address = [
      buildingNumber && `Будинок ${buildingNumber}`,
      apartmentNumber && `Квартира ${apartmentNumber}`,
    ];
    return address.filter(Boolean).join(' · ') || 'Розташування не зазначено';
  }
  protected changeFilters(change: Partial<ResidentFilters>): void {
    this.filtersChange.emit({ ...this.filters, ...change });
  }
}
