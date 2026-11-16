import type { OnDestroy, OnInit } from '@angular/core';
import { Component, inject } from '@angular/core';

import type { DailyStatistic } from '../api.models';
import { ApiService } from '../api.service';
import { errorMessage } from '../error-message';

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
  protected days: 1 | 7 | 30 = 7;
  protected loading = false;
  protected error = '';
  protected items: DailyStatistic[] = [];
  protected chart: ChartDay[] = [];
  protected maxRead = 0;

  ngOnInit(): void {
    void this.load(7);
  }
  ngOnDestroy(): void {
    this.sequence++;
  }
  protected async load(days: 1 | 7 | 30): Promise<void> {
    this.days = days;
    const sequence = ++this.sequence;
    this.loading = true;
    this.error = '';
    try {
      const result = await this.api.getStatistics(days);
      if (sequence !== this.sequence) {
        return;
      }
      this.items = result.items;
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
