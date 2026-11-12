import { inject, Injectable } from '@angular/core';

import type { TicketFilters } from '../api.models';
import { ApiService } from '../api.service';
import type { ResidentFilters, ResidentView } from '../residents/resident-directory';
import { exportFilename } from './export-filename';

@Injectable({ providedIn: 'root' })
export class ExcelExportService {
  private readonly api = inject(ApiService);

  async exportTickets(filters: TicketFilters, isCurrent: () => boolean): Promise<void> {
    const blob = await this.api.exportTickets(filters);
    if (isCurrent()) {
      this.download('Заявки', blob);
    }
  }

  async exportResidents(
    view: ResidentView,
    filters: ResidentFilters,
    pendingBuilding: string,
    isCurrent: () => boolean,
  ): Promise<void> {
    const blob = await (view === 'registered'
      ? this.api.exportResidents(filters)
      : this.api.exportPendingResidents(pendingBuilding));
    if (isCurrent()) {
      this.download(view === 'registered' ? 'Мешканці' : 'Очікують перевірки', blob);
    }
  }

  private download(name: string, blob: Blob): void {
    const url = URL.createObjectURL(blob);
    try {
      const link = document.createElement('a');
      link.href = url;
      link.download = exportFilename(name);
      link.click();
    } finally {
      URL.revokeObjectURL(url);
    }
  }
}
