export class WorkspacePagination {
  index = 0;
  cursor: string | null = null;
  private starts: (string | null)[] = [null];

  get availablePages(): number {
    return this.starts.length;
  }

  start(index: number): string | null | undefined {
    return this.starts.at(index);
  }

  reset(): void {
    this.index = 0;
    this.cursor = null;
    this.starts = [null];
  }

  accept(index: number, start: string | null, cursor: string | null): void {
    this.index = index;
    this.starts[index] = start;
    if (cursor) {
      if (this.starts[index + 1] !== cursor) {
        this.starts = this.starts.slice(0, index + 1);
      }
      this.starts[index + 1] = cursor;
    } else {
      this.starts = this.starts.slice(0, index + 1);
    }
    this.cursor = cursor;
  }
}
