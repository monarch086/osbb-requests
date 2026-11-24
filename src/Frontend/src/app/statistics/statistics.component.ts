import type { OnDestroy, OnInit } from '@angular/core';
import { Component, inject } from '@angular/core';

import type { DailyStatistic } from '../api.models';
import { ApiService } from '../api.service';
import { errorMessage } from '../error-message';
import type { EndpointStatistic, SortDirection, StatisticsSortColumn } from './statistics-sort';
import { aggregateStatistics, readUnitsPerRequest, sortStatistics } from './statistics-sort';

interface ChartDay {
  date: string;
  requests: number;
  readUnits: number;
  writeUnits: number;
}

@Component({
  selector: 'app-statistics',
  templateUrl: './statistics.component.html',
})
export class StatisticsComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private sequence = 0;
  private dailyItems: DailyStatistic[] = [];
  protected days: 1 | 7 | 30 = 7;
  protected loading = false;
  protected error = '';
  protected items: EndpointStatistic[] = [];
  protected chart: ChartDay[] = [];
  protected maxRead = 0;
  protected selectedDay: string | null = null;
  protected sortColumn: StatisticsSortColumn | null = null;
  protected sortDirection: SortDirection = 'descending';
  protected readonly readUnitsPerRequest = readUnitsPerRequest;

  protected sort(column: StatisticsSortColumn): void {
    this.sortDirection =
      this.sortColumn === column && this.sortDirection === 'descending'
        ? 'ascending'
        : 'descending';
    this.sortColumn = column;
    this.items = sortStatistics(this.items, column, this.sortDirection);
  }

  protected selectDay(day: string): void {
    this.selectedDay = this.selectedDay === day ? null : day;
    this.updateItems();
  }

  protected clearDay(): void {
    this.selectedDay = null;
    this.updateItems();
  }

  private updateItems(): void {
    const totals = aggregateStatistics(this.dailyItems, this.selectedDay);
    this.items = this.sortColumn
      ? sortStatistics(totals, this.sortColumn, this.sortDirection)
      : totals;
  }

  ngOnInit(): void {
    void this.load(7);
  }
  ngOnDestroy(): void {
    this.sequence++;
  }
  protected async load(days: 1 | 7 | 30): Promise<void> {
    if (days !== this.days) {
      this.selectedDay = null;
    }
    this.days = days;
    const sequence = ++this.sequence;
    this.loading = true;
    this.error = '';
    try {
      const result = await this.api.getStatistics(days);
      if (sequence !== this.sequence) {
        return;
      }
      this.dailyItems = result.items;
      const today = new Date();
      this.chart = Array.from({ length: days }, (_, i) => {
        const date = new Date(
          Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - days + 1 + i),
        )
          .toISOString()
          .slice(0, 10);
        const rows = result.items.filter((row) => row.date === date);
        return {
          date,
          requests: rows.reduce((total, row) => total + row.requests, 0),
          readUnits: rows.reduce((total, row) => total + row.readUnits, 0),
          writeUnits: rows.reduce((total, row) => total + row.writeUnits, 0),
        };
      });
      this.maxRead = Math.max(0, ...this.chart.map((day) => day.readUnits));
      if (this.selectedDay && !this.chart.some((day) => day.date === this.selectedDay)) {
        this.selectedDay = null;
      }
      this.updateItems();
    } catch (error) {
      if (sequence === this.sequence) {
        this.error = errorMessage(error);
      }
    } finally {
      if (sequence === this.sequence) {
        this.loading = false;
      }
    }
  }
  protected format(value: number): string {
    return new Intl.NumberFormat('uk-UA', { maximumFractionDigits: 2 }).format(value);
  }
}
