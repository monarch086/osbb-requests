import type { OnDestroy } from '@angular/core';
import { Component, inject } from '@angular/core';

import type { ApartmentReviewEntry } from '../api.models';
import { ApiService } from '../api.service';
import { errorMessage } from '../error-message';
import { formatDate } from '../shared/ticket-presentation';

@Component({
  selector: 'app-apartment-reviews',
  templateUrl: './apartment-reviews.component.html',
})
export class ApartmentReviewsComponent implements OnDestroy {
  private readonly api = inject(ApiService);
  private active = true;
  protected readonly date = formatDate;
  protected open = false;
  protected loading = false;
  protected error = '';
  protected notice = '';
  protected entries: ApartmentReviewEntry[] = [];
  protected cursor: string | null = null;
  protected position: string | null = null;

  ngOnDestroy(): void {
    this.active = false;
  }

  protected async toggle(): Promise<void> {
    this.open = !this.open;
    if (this.open) {
      await this.load(null);
    }
  }

  protected async load(cursor: string | null): Promise<void> {
    if (this.loading) {
      return;
    }
    this.loading = true;
    this.error = '';
    try {
      const page = await this.api.getApartmentReviews(cursor);
      if (this.active) {
        this.entries = page.items;
        this.cursor = page.cursor;
        this.position = cursor;
      }
    } catch (error) {
      if (this.active) {
        this.error = errorMessage(error);
      }
    } finally {
      this.loading = false;
    }
  }

  protected async decide(
    entry: ApartmentReviewEntry,
    id: string,
    approved: boolean,
  ): Promise<void> {
    if (this.loading) {
      return;
    }
    this.loading = true;
    this.error = '';
    this.notice = '';
    try {
      await this.api.decideApartment(entry.registrationId, id, approved);
      if (this.active) {
        const remaining = entry.apartments.filter((apartment) => apartment.id !== id);
        this.entries = this.entries
          .map((item) => (item === entry ? { ...entry, apartments: remaining } : item))
          .filter((item) => item.apartments.length);
        this.notice = approved ? 'Квартиру підтверджено.' : 'Запит відхилено.';
      }
    } catch (error) {
      if (this.active) {
        this.error = errorMessage(error);
      }
    } finally {
      this.loading = false;
    }
  }
}
