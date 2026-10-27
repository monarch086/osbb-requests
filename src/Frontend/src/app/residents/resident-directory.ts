import type { RegisteredResident } from '../api.service';

export interface ResidentFilters {
  sort: 'registeredAt' | 'lastName';
  direction: 'asc' | 'desc';
  building: string;
}

const names = new Intl.Collator('uk-UA', { sensitivity: 'accent' });

export class ResidentDirectory {
  filters: ResidentFilters = { sort: 'registeredAt', direction: 'desc', building: '' };
  private residents: RegisteredResident[] = [];
  private filtered: RegisteredResident[] = [];
  private readonly pageSize = 25;

  get pageCount(): number {
    return Math.max(1, Math.ceil(this.filtered.length / this.pageSize));
  }

  setItems(items: RegisteredResident[]): void {
    this.residents = [...new Map(items.map((r) => [r.telegramChatId || r.id, r])).values()];
    this.applyFilters(this.filters);
  }

  applyFilters(filters: ResidentFilters): void {
    this.filters = { ...filters };
    const direction = filters.direction === 'asc' ? 1 : -1;
    this.filtered = this.residents
      .filter((resident) =>
        filters.building === 'Unspecified'
          ? !resident.buildingNumber
          : !filters.building || resident.buildingNumber === filters.building,
      )
      .sort((a, b) => {
        let comparison: number;
        if (filters.sort === 'lastName') {
          const aName = a.lastName?.trim() || '';
          const bName = b.lastName?.trim() || '';
          if (!aName !== !bName) {
            return aName ? -1 : 1;
          }
          comparison =
            names.compare(aName, bName) || names.compare(a.firstName || '', b.firstName || '');
        } else {
          comparison = Date.parse(a.registeredAt) - Date.parse(b.registeredAt);
        }
        return comparison * direction || a.id.localeCompare(b.id);
      });
  }

  page(index: number): RegisteredResident[] {
    return this.filtered.slice(index * this.pageSize, (index + 1) * this.pageSize);
  }
}
