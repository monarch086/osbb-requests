import type { OnDestroy, OnInit } from '@angular/core';
import { Directive, EventEmitter, inject, Input, Output } from '@angular/core';

import type { Admin } from '../api.models';
import { ApiService } from '../api.service';
import { errorMessage } from '../error-message';
import type { WorkspaceLists } from './workspace-lists';

@Directive()
export abstract class WorkspaceSection implements OnInit, OnDestroy {
  @Input({ required: true }) me!: Admin;
  @Output() readonly savingChange = new EventEmitter<boolean>();
  protected readonly api = inject(ApiService);
  protected abstract readonly lists: WorkspaceLists;
  protected loading = false;
  protected saving = false;
  protected error = '';
  protected notice = '';
  protected loadSequence = 0;
  protected epoch = 0;

  ngOnInit(): void {
    void this.load();
  }
  ngOnDestroy(): void {
    this.epoch++;
    this.loadSequence++;
  }

  protected async load(): Promise<void> {
    if (this.loading || this.saving) {
      return;
    }
    await this.loadPage(
      this.lists.pagination.index,
      this.lists.pagination.start(this.lists.pagination.index) ?? null,
    );
  }
  protected async previousPage(): Promise<void> {
    await this.goToPage(this.lists.pagination.index);
  }
  protected async nextPage(): Promise<void> {
    if (!this.lists.hasNext()) {
      return;
    }
    await this.goToPage(this.lists.pagination.index + 2);
  }
  protected async goToPage(number: number): Promise<void> {
    if (
      this.loading ||
      this.saving ||
      !Number.isInteger(number) ||
      number < 1 ||
      number > this.lists.availablePages() ||
      number === this.lists.pagination.index + 1
    ) {
      return;
    }
    const index = number - 1;
    if (this.lists.isRegisteredView()) {
      this.lists.showResidentPage(index);
      return;
    }
    const start = this.lists.pagination.start(index);
    if (start === undefined || (index > 0 && start === null)) {
      return;
    }
    await this.loadPage(index, start);
  }
  protected async loadPage(index: number, startCursor: string | null): Promise<void> {
    if (!this.me.isActive || this.saving) {
      return;
    }
    const sequence = ++this.loadSequence;
    const epoch = this.epoch;
    this.loading = true;
    this.error = '';
    const isCurrent = (): boolean => epoch === this.epoch && sequence === this.loadSequence;
    try {
      await this.loadItems(index, startCursor, isCurrent);
    } catch (e) {
      if (epoch === this.epoch && sequence === this.loadSequence) {
        this.error = errorMessage(e);
      }
    } finally {
      if (epoch === this.epoch && sequence === this.loadSequence) {
        this.loading = false;
      }
    }
  }
  protected abstract loadItems(
    index: number,
    startCursor: string | null,
    isCurrent: () => boolean,
  ): Promise<void>;

  protected async write(action: () => Promise<void>): Promise<void> {
    if (this.saving) {
      return;
    }
    this.saving = true;
    this.savingChange.emit(true);
    this.error = '';
    this.notice = '';
    try {
      await action();
    } catch (e) {
      this.error = errorMessage(e);
    } finally {
      this.saving = false;
      this.savingChange.emit(false);
    }
  }
  protected async runExport(action: (isCurrent: () => boolean) => Promise<void>): Promise<void> {
    if (!this.me.isActive || this.loading || this.saving) {
      return;
    }
    const epoch = this.epoch;
    const sequence = ++this.loadSequence;
    const isCurrent = (): boolean => epoch === this.epoch && sequence === this.loadSequence;
    this.loading = true;
    this.error = '';
    try {
      await action(isCurrent);
    } catch (error) {
      if (isCurrent()) {
        this.error = errorMessage(error);
      }
    } finally {
      if (isCurrent()) {
        this.loading = false;
      }
    }
  }
}
