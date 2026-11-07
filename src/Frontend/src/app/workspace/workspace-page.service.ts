import { inject, Injectable } from '@angular/core';

import type { RegisteredResident, TicketFilters } from '../api.models';
import { ApiService } from '../api.service';
import { collectPages } from '../export/collect-pages';
import type { WorkspacePage, WorkspaceTab } from './workspace-page.models';

@Injectable()
export class WorkspacePageService {
  private readonly api = inject(ApiService);

  load(tab: WorkspaceTab, filters: TicketFilters, cursor: string | null): Promise<WorkspacePage> {
    switch (tab) {
      case 'requests':
        return this.api.getTickets(filters, cursor).then((page) => ({ kind: 'requests', page }));
      case 'residents':
        return this.api.getRegistrations(cursor).then((page) => ({ kind: 'residents', page }));
      case 'messages':
        return this.api.getMessages(cursor).then((page) => ({ kind: 'messages', page }));
      case 'admins':
        return this.api.getAdministrators(cursor).then((page) => ({ kind: 'admins', page }));
    }
  }

  residents(isCurrent: () => boolean): Promise<RegisteredResident[] | null> {
    return collectPages((cursor) => this.api.getResidents(cursor), isCurrent);
  }
}
