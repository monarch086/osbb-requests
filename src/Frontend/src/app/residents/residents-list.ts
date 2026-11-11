import type { RegisteredResident, Registration } from '../api.models';
import { WorkspaceLists } from '../workspace/workspace-lists';
import { PendingDirectory } from './pending-directory';
import type { ResidentView } from './resident-directory';
import { ResidentDirectory } from './resident-directory';

export class ResidentsList extends WorkspaceLists {
  residents: Registration[] = [];
  registeredResidents: RegisteredResident[] = [];
  residentView: ResidentView = 'registered';
  residentDirectory = new ResidentDirectory();
  pendingDirectory = new PendingDirectory();
  override usesLocalPagination(): boolean {
    return true;
  }
  override availablePages(): number {
    return this.residentView === 'registered'
      ? this.residentDirectory.pageCount
      : this.pendingDirectory.pageCount;
  }
  override hasNext(): boolean {
    return this.pagination.index + 1 < this.availablePages();
  }
  override itemCount(): number {
    return this.residentView === 'registered'
      ? this.registeredResidents.length
      : this.residents.length;
  }
  override showResidentPage(index: number): void {
    this.pagination.index = Math.min(index, this.availablePages() - 1);
    if (this.residentView === 'registered') {
      this.registeredResidents = this.residentDirectory.page(this.pagination.index);
    } else {
      this.residents = this.pendingDirectory.page(this.pagination.index);
    }
  }
  override resetPagination(): void {
    this.pagination.reset();
    this.residents = [];
    this.registeredResidents = [];
    this.residentDirectory.setItems([]);
    this.pendingDirectory.setItems([]);
  }
}
