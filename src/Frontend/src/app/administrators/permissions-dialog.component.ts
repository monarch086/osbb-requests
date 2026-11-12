import type { OnInit } from '@angular/core';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

import type { Admin, PagePermissions } from '../api.models';

@Component({
  selector: 'app-permissions-dialog',
  imports: [FormsModule],
  templateUrl: './permissions-dialog.component.html',
})
export class PermissionsDialogComponent implements OnInit {
  @Input({ required: true }) admin!: Admin;
  @Input() saving = false;
  @Input() error = '';
  @Output() readonly saved = new EventEmitter<PagePermissions>();
  @Output() readonly closed = new EventEmitter<void>();
  protected permissions!: PagePermissions;
  ngOnInit(): void {
    this.permissions = { ...this.admin.effectivePermissions };
  }
}
