import { WorkspacePagination } from './workspace-pagination';

export abstract class WorkspaceLists {
  readonly pagination = new WorkspacePagination();

  abstract resetPagination(): void;
  abstract itemCount(): number;

  isRegisteredView(): boolean {
    return false;
  }
  showResidentPage(index: number): void {
    throw new Error(`Resident page ${index} is unavailable`);
  }
  availablePages(): number {
    return this.pagination.availablePages;
  }
  hasNext(): boolean {
    return !!this.pagination.cursor;
  }
}
