export class PendingCountCache {
  private value: number | null = null;
  private expiresAt = 0;

  get(now = Date.now()): number | null {
    return now < this.expiresAt ? this.value : null;
  }

  set(value: number, now = Date.now()): void {
    this.value = value;
    this.expiresAt = now + 30_000;
  }

  clear(): void {
    this.value = null;
    this.expiresAt = 0;
  }
}
