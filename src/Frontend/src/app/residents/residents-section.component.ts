import { Component, inject } from '@angular/core';

import type {
  RegisteredResident,
  Registration,
  RegistrationApproval,
  ResidentDataChange,
  ResidentTypeHistory,
} from '../api.models';
import { errorMessage } from '../error-message';
import { collectPages } from '../export/collect-pages';
import { ExcelExportService } from '../export/excel-export.service';
import { PaginationComponent } from '../pagination/pagination.component';
import { RegistrationRejectionDialogComponent } from '../registration-rejection-dialog/registration-rejection-dialog.component';
import { WorkspaceSection } from '../workspace/workspace-section';
import { ResidentDetailsDialogComponent } from './resident-details-dialog.component';
import type { ResidentFilters, ResidentView } from './resident-directory';
import { ResidentHistoryDialogComponent } from './resident-history-dialog.component';
import { ResidentTypeDialogComponent } from './resident-type-dialog.component';
import { ResidentsComponent } from './residents.component';
import { ResidentsList } from './residents-list';

@Component({
  selector: 'app-residents-section',
  imports: [
    ResidentsComponent,
    RegistrationRejectionDialogComponent,
    PaginationComponent,
    ResidentTypeDialogComponent,
    ResidentHistoryDialogComponent,
    ResidentDetailsDialogComponent,
  ],
  templateUrl: './residents-section.component.html',
})
export class ResidentsSectionComponent extends WorkspaceSection {
  private readonly excel = inject(ExcelExportService);
  protected override readonly lists = new ResidentsList();
  protected approval: RegistrationApproval | null = null;
  protected approvalLoading = false;
  protected approvalError = false;
  private approvalSequence = 0;
  protected selectedPending: Registration | null = null;
  protected selectedRegistered: RegisteredResident | null = null;
  protected rejecting: Registration | null = null;
  private detailsTrigger: HTMLElement | null = null;
  protected changingType: Registration | RegisteredResident | null = null;
  protected historyResident: Registration | RegisteredResident | null = null;
  protected history: ResidentTypeHistory[] = [];
  protected historyLoading = false;
  protected historyError = '';
  private historySequence = 0;
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
  protected openPending(resident: Registration): void {
    this.detailsTrigger =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.selectedPending = resident;
    this.selectedRegistered = null;
    this.error = '';
  }
  protected async openRegistered(resident: RegisteredResident): Promise<void> {
    this.detailsTrigger =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.selectedRegistered = resident;
    this.selectedPending = null;
    this.error = '';
    this.approval = null;
    this.approvalError = false;
    this.approvalLoading = true;
    const sequence = ++this.approvalSequence;
    const epoch = this.epoch;
    const current = (): boolean => sequence === this.approvalSequence && epoch === this.epoch;
    try {
      const approval = await this.api.getRegistrationApproval(resident.id);
      if (current()) {
        this.approval = approval;
      }
    } catch {
      if (current()) {
        this.approvalError = true;
      }
    } finally {
      if (current()) {
        this.approvalLoading = false;
      }
    }
  }
  protected closeDetails(): void {
    if (this.saving) {
      return;
    }
    this.approvalSequence++;
    this.approvalLoading = false;
    this.approval = null;
    this.selectedPending = null;
    this.selectedRegistered = null;
    this.error = '';
    if (this.detailsTrigger?.isConnected) {
      this.detailsTrigger.focus();
    }
    this.detailsTrigger = null;
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
      this.selectedPending = null;
      this.notice = approved ? 'Мешканцю надано доступ.' : 'Реєстрацію відхилено.';
    });
  }
  protected async saveResident(
    resident: Registration | RegisteredResident,
    change: ResidentDataChange,
  ): Promise<void> {
    if (!this.me.effectivePermissions.pendingResidents || this.saving) {
      return;
    }
    await this.write(async () => {
      await this.api.updateResidentData(resident, change);
      resident.firstName = change.firstName;
      resident.lastName = change.lastName;
      resident.residentType = change.residentType;
      resident.typeChangeCount = (resident.typeChangeCount ?? 0) + 1;
      resident.version++;
      this.lists.residentDirectory.applyFilters(this.lists.residentDirectory.filters);
      this.lists.showResidentPage(this.lists.pagination.index);
      this.changingType = null;
      this.notice = 'Дані мешканця збережено.';
    });
  }
  protected async openHistory(resident: Registration | RegisteredResident): Promise<void> {
    if (this.loading || this.saving) {
      return;
    }
    this.historyResident = resident;
    this.history = [];
    this.historyError = '';
    this.historyLoading = true;
    const sequence = ++this.historySequence;
    const epoch = this.epoch;
    const current = (): boolean => sequence === this.historySequence && epoch === this.epoch;
    try {
      const history = await collectPages(
        (cursor) => this.api.getResidentHistory(resident.id, cursor),
        current,
      );
      if (history && current()) {
        this.history = history;
      }
    } catch (error) {
      if (current()) {
        this.historyError = errorMessage(error);
      }
    } finally {
      if (current()) {
        this.historyLoading = false;
      }
    }
  }
  protected closeHistory(): void {
    this.historySequence++;
    this.historyResident = null;
    this.historyLoading = false;
  }
  protected closeDialog(): void {
    if (!this.saving) {
      this.rejecting = null;
    }
  }
}
