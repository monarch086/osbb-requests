import { Component, EventEmitter, Input, Output } from '@angular/core';

@Component({
  selector: 'app-pagination',
  templateUrl: './pagination.component.html',
})
export class PaginationComponent {
  @Input() pageNumber = 1;
  @Input() itemCount = 0;
  @Input() hasPrevious = false;
  @Input() hasNext = false;
  @Input() disabled = false;
  @Output() previousPage = new EventEmitter<void>();
  @Output() nextPage = new EventEmitter<void>();
}
