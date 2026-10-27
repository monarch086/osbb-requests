import { Component, EventEmitter, Input, Output } from '@angular/core';

@Component({
  selector: 'app-pagination',
  styleUrl: './pagination.component.scss',
  templateUrl: './pagination.component.html',
})
export class PaginationComponent {
  @Input() pageNumber = 1;
  @Input() itemCount = 0;
  @Input() availablePages = 1;
  @Input() hasPrevious = false;
  @Input() hasNext = false;
  @Input() disabled = false;
  @Output() readonly pageSelected = new EventEmitter<number>();
  @Output() readonly previousPage = new EventEmitter<void>();
  @Output() readonly nextPage = new EventEmitter<void>();

  protected get pageLinks(): { key: string; number: number | null }[] {
    const count = Math.max(1, this.availablePages);
    const windowSize = 10;
    const start = Math.max(1, Math.min(this.pageNumber - 4, count - windowSize + 1));
    const end = Math.min(count, start + windowSize - 1);
    const links: { key: string; number: number | null }[] = [];
    const add = (number: number): void => {
      links.push({ key: String(number), number });
    };
    if (start > 1) {
      add(1);
      if (start > 2) {
        links.push({ key: 'before', number: null });
      }
    }
    for (let number = start; number <= end; number++) {
      add(number);
    }
    if (end < count) {
      if (end < count - 1) {
        links.push({ key: 'after', number: null });
      }
      add(count);
    }
    return links;
  }
  protected selectPage(number: number | null): void {
    if (!this.disabled && number !== null) {
      this.pageSelected.emit(number);
    }
  }
}
