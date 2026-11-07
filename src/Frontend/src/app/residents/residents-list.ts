import type { RegisteredResident, Registration } from '../api.models';
import { WorkspaceLists } from '../workspace/workspace-lists';
import type { ResidentView } from './resident-directory';
import { ResidentDirectory } from './resident-directory';

export class ResidentsList extends WorkspaceLists {
  residents: Registration[] = [];
  registeredResidents: RegisteredResident[] = [];
  residentView: ResidentView = 'registered';
  residentDirectory = new ResidentDirectory();
  override isRegisteredView(): boolean {
    return this.residentView === 'registered';
  }
  override availablePages(): number {
    return this.isRegisteredView()
      ? this.residentDirectory.pageCount
      : this.pagination.availablePages;
  }
  override hasNext(): boolean {
    return this.isRegisteredView()
      ? this.pagination.index + 1 < this.availablePages()
      : !!this.pagination.cursor;
  }
  override itemCount(): number {
    return this.isRegisteredView() ? this.registeredResidents.length : this.residents.length;
  }
  override showResidentPage(index: number): void {
    this.pagination.index = Math.min(index, this.residentDirectory.pageCount - 1);
    this.registeredResidents = this.residentDirectory.page(this.pagination.index);
  }
  override resetPagination(): void {
    this.pagination.reset();
    this.residents = [];
    this.registeredResidents = [];
    this.residentDirectory.setItems([]);
  }
}
