import type { AfterViewInit, ElementRef, OnInit } from '@angular/core';
import { Component, EventEmitter, Input, Output, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgOptionComponent, NgSelectComponent } from '@ng-select/ng-select';

import type { ResidentKey, ResidentKeyUpdate } from '../api.models';

@Component({
  selector: 'app-resident-key-dialog',
  imports: [FormsModule, NgSelectComponent, NgOptionComponent],
  templateUrl: './resident-key-dialog.component.html',
})
export class ResidentKeyDialogComponent implements OnInit, AfterViewInit {
  @Input({ required: true }) key!: ResidentKey;
  @Input() remove = false;
  @Input() saving = false;
  @Input() error = '';
  @Output() readonly saved = new EventEmitter<ResidentKeyUpdate>();
  @Output() readonly closed = new EventEmitter<void>();
  private readonly cancel = viewChild.required<ElementRef<HTMLButtonElement>>('cancel');
  protected type = '';
  protected serialNumber = '';

  ngOnInit(): void {
    this.type = this.key.type;
    this.serialNumber = this.key.serialNumber ?? '';
  }

  ngAfterViewInit(): void {
    this.cancel().nativeElement.focus();
  }

  protected submit(): void {
    if (!this.saving) {
      this.saved.emit(
        this.remove ? { remove: true } : { type: this.type, serialNumber: this.serialNumber },
      );
    }
  }
}
