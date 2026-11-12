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
  override ngOnInit(): void {
    if (
      !this.me.effectivePermissions.registeredResidents &&
      this.me.effectivePermissions.pendingResidents
    ) {
      this.lists.residentView = 'pending';
    }
    super.ngOnInit();
  }
  protected override async loadItems(
    index: number,
    _cursor: string | null,
    isCurrent: () => boolean,
  ): Promise<void> {
    const count = this.me.effectivePermissions.pendingResidents
      ? await this.api.getRegistrationCount()
      : { count: 0 };
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
    const registrations = await collectPages((next) => this.api.getRegistrations(next), isCurrent);
    if (registrations) {
      this.lists.pendingDirectory.setItems(registrations);
      this.lists.showResidentPage(index);
    }
  }
  protected async switchResidentView(view: ResidentView): Promise<void> {
    if (
      this.saving ||
      this.loading ||
      view === this.lists.residentView ||
      (view === 'registered' && !this.me.effectivePermissions.registeredResidents) ||
      (view === 'pending' && !this.me.effectivePermissions.pendingResidents)
    ) {
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
      this.excel.exportResidents(
        this.lists.residentView,
        this.lists.residentDirectory,
        this.lists.pendingDirectory,
        current,
      ),
    );
  }
  protected applyResidentFilters(filters: ResidentFilters): void {
    if (this.loading || this.saving || this.lists.residentView !== 'registered') {
      return;
    }
    this.lists.residentDirectory.applyFilters(filters);
    this.lists.showResidentPage(0);
  }
  protected applyPendingBuilding(building: string): void {
    if (this.loading || this.saving || this.lists.residentView !== 'pending') {
      return;
    }
    this.lists.pendingDirectory.building = building;
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
      this.lists.pendingDirectory.remove(registration.id);
      this.lists.showResidentPage(this.lists.pagination.index);
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
