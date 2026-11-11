import type { Registration } from '../api.models';

export class PendingDirectory {
  building = '';
  private items: Registration[] = [];
  private readonly pageSize = 25;

  get pageCount(): number {
    return Math.max(1, Math.ceil(this.all().length / this.pageSize));
  }

  setItems(items: Registration[]): void {
    this.items = [...items];
  }

  remove(id: string): void {
    this.items = this.items.filter((item) => item.id !== id);
  }

  all(): Registration[] {
    return this.items.filter((item) =>
      this.building === 'Unspecified'
        ? !item.buildingNumber
        : !this.building || item.buildingNumber === this.building,
    );
  }

  page(index: number): Registration[] {
    return this.all().slice(index * this.pageSize, (index + 1) * this.pageSize);
  }
}
