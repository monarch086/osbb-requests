import type { RequestCounts } from './request-counts';

export class RequestCountsCache {
  private value: RequestCounts | null = null;
  private expiresAt = 0;

  get(now = Date.now()): RequestCounts | null {
    return now < this.expiresAt ? this.value : null;
  }

  set(value: RequestCounts, now = Date.now()): void {
    this.value = value;
    this.expiresAt = now + 30_000;
  }

  clear(): void {
    this.value = null;
    this.expiresAt = 0;
  }
}
