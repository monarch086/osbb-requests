import { inject, Injectable } from '@angular/core';

import type { TicketFilters } from '../api.models';
import { ApiService } from '../api.service';
import type { PendingDirectory } from '../residents/pending-directory';
import type { ResidentDirectory, ResidentView } from '../residents/resident-directory';
import { resolveExcelModule } from './excel-module';
import { pendingResidentRows, registeredResidentRows, residentHeadings } from './export-data';
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
    directory: ResidentDirectory,
    pendingDirectory: PendingDirectory,
    isCurrent: () => boolean,
  ): Promise<void> {
    if (view === 'registered') {
      await this.save(
        'Мешканці',
        [...residentHeadings, 'Дата реєстрації'],
        registeredResidentRows(directory.all()),
        isCurrent,
      );
    } else {
      await this.save(
        'Очікують перевірки',
        [...residentHeadings, 'Дата подання'],
        pendingResidentRows(pendingDirectory.all()),
        isCurrent,
      );
    }
  }

  private async save(
    name: string,
    headings: string[],
    rows: (string | number)[][],
    isCurrent: () => boolean,
  ): Promise<void> {
    const excel = resolveExcelModule(await import('exceljs'));
    if (!isCurrent()) {
      return;
    }
    const workbook = new excel.Workbook();
    const sheet = workbook.addWorksheet(name);
    sheet.addRow(headings);
    sheet.addRows(rows);
    sheet.getRow(1).font = { bold: true };
    sheet.views = [{ state: 'frozen', ySplit: 1 }];
    sheet.columns.forEach((column) => {
      column.width = 24;
    });
    const buffer = await workbook.xlsx.writeBuffer();
    if (!isCurrent()) {
      return;
    }
    const blob = new Blob([new Uint8Array(buffer)], {
      type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    });
    this.download(name, blob);
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
