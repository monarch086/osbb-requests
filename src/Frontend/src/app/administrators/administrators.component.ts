import { Component, EventEmitter, Input, Output } from '@angular/core';

import { AdministratorNotificationsComponent } from '../administrator-notifications/administrator-notifications.component';
import type { Admin, NotificationSettings, PagePermissions } from '../api.models';
import { PermissionsDialogComponent } from './permissions-dialog.component';

@Component({
  selector: 'app-administrators',
  imports: [AdministratorNotificationsComponent, PermissionsDialogComponent],
  templateUrl: './administrators.component.html',
})
export class AdministratorsComponent {
  @Input() admins: Admin[] = [];
  @Input() saving = false;
  @Input() error = '';
  @Output() readonly saveNotifications = new EventEmitter<{
    admin: Admin;
    settings: NotificationSettings;
  }>();
  @Output() readonly toggleAccess = new EventEmitter<Admin>();
  @Output() readonly savePermissions = new EventEmitter<{
    admin: Admin;
    permissions: PagePermissions;
  }>();
  protected notifications: Admin | null = null;
  protected permissions: Admin | null = null;
  closeDialogs(): void {
    this.notifications = null;
    this.permissions = null;
  }
}
