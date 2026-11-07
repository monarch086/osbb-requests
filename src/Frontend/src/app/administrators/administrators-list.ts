import type { Admin } from '../api.models';
import { WorkspaceLists } from '../workspace/workspace-lists';

export class AdministratorsList extends WorkspaceLists {
  admins: Admin[] = [];
  override itemCount(): number {
    return this.admins.length;
  }
  override resetPagination(): void {
    this.pagination.reset();
    this.admins = [];
  }
}
