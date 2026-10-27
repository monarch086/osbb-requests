import { Component, EventEmitter, Input, Output } from '@angular/core';

import { AdministratorNotificationsComponent } from '../administrator-notifications/administrator-notifications.component';
import type { Admin, NotificationSettings } from '../api.models';

@Component({
  selector: 'app-administrators',
  imports: [AdministratorNotificationsComponent],
  templateUrl: './administrators.component.html',
})
export class AdministratorsComponent {
  @Input() admins: Admin[] = [];
  @Input() saving = false;
  @Output() readonly saveNotifications = new EventEmitter<{
    admin: Admin;
    settings: NotificationSettings;
  }>();
  @Output() readonly toggleAccess = new EventEmitter<Admin>();
}
