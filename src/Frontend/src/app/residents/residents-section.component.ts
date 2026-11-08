import { Component, inject } from '@angular/core';

import type { Registration } from '../api.models';
import { collectPages } from '../export/collect-pages';
import { ExcelExportService } from '../export/excel-export.service';
import { PaginationComponent } from '../pagination/pagination.component';
import { RegistrationRejectionDialogComponent } from '../registration-rejection-dialog/registration-rejection-dialog.component';
import { WorkspaceSection } from '../workspace/workspace-section';
import type { ResidentFilters, ResidentView } from './resident-directory';
import { ResidentsComponent } from './residents.component';
import { ResidentsList } from './residents-list';

@Component({
  selector: 'app-residents-section',
  imports: [ResidentsComponent, RegistrationRejectionDialogComponent, PaginationComponent],
  templateUrl: './residents-section.component.html',
})
export class ResidentsSectionComponent extends WorkspaceSection {
  private readonly excel = inject(ExcelExportService);
  protected override readonly lists = new ResidentsList();
  protected rejecting: Registration | null = null;
  protected pendingCount = 0;
  protected override async loadItems(
    index: number,
    cursor: string | null,
    isCurrent: () => boolean,
  ): Promise<void> {
    const count = await this.api.getRegistrationCount();
    if (!isCurrent()) {
      return;
    }
    this.pendingCount = count.count;
    if (this.lists.residentView === 'registered') {
      const residents = await collectPages((next) => this.api.getResidents(next), isCurrent);
      if (residents) {
        this.lists.residentDirectory.setItems(residents);
        this.lists.showResidentPage(index);
      }
      return;
    }
    const page = await this.api.getRegistrations(cursor);
    if (isCurrent()) {
      this.lists.residents = page.items;
      this.lists.pagination.accept(index, cursor, page.cursor);
    }
  }
  protected async switchResidentView(view: ResidentView): Promise<void> {
    if (this.saving || this.loading || view === this.lists.residentView) {
      return;
    }
    this.lists.residentView = view;
    this.lists.resetPagination();
    this.rejecting = null;
    this.notice = '';
    await this.load();
  }
  protected async exportExcel(): Promise<void> {
    await this.runExport((current) =>
      this.excel.exportResidents(this.lists.residentView, this.lists.residentDirectory, current),
    );
  }
  protected applyResidentFilters(filters: ResidentFilters): void {
    if (this.loading || this.saving || !this.lists.isRegisteredView()) {
      return;
    }
    this.lists.residentDirectory.applyFilters(filters);
    this.lists.showResidentPage(0);
  }
  protected async decide(
    registration: Registration,
    approved: boolean,
    reason = '',
  ): Promise<void> {
    if (!approved && !reason.trim()) {
      this.error = 'Вкажіть причину відхилення.';
      return;
    }
    await this.write(async () => {
      await this.api.decideRegistration(registration, approved, approved ? null : reason.trim());
      this.lists.residents = this.lists.residents.filter((r) => r.id !== registration.id);
      this.pendingCount = Math.max(0, this.pendingCount - 1);
      this.rejecting = null;
      this.notice = approved ? 'Мешканцю надано доступ.' : 'Реєстрацію відхилено.';
    });
  }
  protected closeDialog(): void {
    if (!this.saving) {
      this.rejecting = null;
    }
  }
}
