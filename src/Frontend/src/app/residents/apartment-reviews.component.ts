import type { OnChanges, OnDestroy, SimpleChanges } from '@angular/core';
import { Component, EventEmitter, inject, Input, Output } from '@angular/core';

import type { ApartmentReviewEntry } from '../api.models';
import { ApiService } from '../api.service';
import { errorMessage } from '../error-message';
import { formatDate } from '../shared/ticket-presentation';

@Component({
  selector: 'app-apartment-reviews',
  templateUrl: './apartment-reviews.component.html',
})
export class ApartmentReviewsComponent implements OnChanges, OnDestroy {
  private readonly api = inject(ApiService);
  private active = true;
  protected readonly date = formatDate;
  @Input() refreshVersion = 0;
  @Input() countLoading = false;
  @Output() readonly decided = new EventEmitter<void>();
  @Output() readonly savingChange = new EventEmitter<boolean>();
  private loadSequence = 0;
  protected loading = false;
  protected error = '';
  protected notice = '';
  protected entries: ApartmentReviewEntry[] = [];
  protected cursor: string | null = null;
  protected position: string | null = null;

  ngOnDestroy(): void {
    this.active = false;
  }

  ngOnChanges(changes: Partial<SimpleChanges>): void {
    if (changes['refreshVersion']) {
      void this.load(null);
    }
  }

  protected async load(cursor: string | null): Promise<void> {
    const sequence = ++this.loadSequence;
    const current = (): boolean => this.active && sequence === this.loadSequence;
    this.loading = true;
    this.error = '';
    try {
      const page = await this.api.getApartmentReviews(cursor);
      if (current()) {
        this.entries = page.items;
        this.cursor = page.cursor;
        this.position = cursor;
      }
    } catch (error) {
      if (current()) {
        this.error = errorMessage(error);
      }
    } finally {
      if (current()) {
        this.loading = false;
      }
    }
  }

  protected async decide(
    entry: ApartmentReviewEntry,
    id: string,
    approved: boolean,
  ): Promise<void> {
    if (this.loading || this.countLoading) {
      return;
    }
    this.loading = true;
    this.savingChange.emit(true);
    this.error = '';
    this.notice = '';
    try {
      await this.api.decideApartment(entry.registrationId, id, approved);
      if (this.active) {
        const remaining = entry.apartments.filter((apartment) => apartment.id !== id);
        this.entries = this.entries
          .map((item) => (item === entry ? { ...entry, apartments: remaining } : item))
          .filter((item) => item.apartments.length);
        this.decided.emit();
        this.notice = approved ? 'Квартиру підтверджено.' : 'Запит відхилено.';
      }
    } catch (error) {
      if (this.active) {
        this.error = errorMessage(error);
      }
    } finally {
      this.loading = false;
      if (this.active) {
        this.savingChange.emit(false);
      }
    }
  }
}
