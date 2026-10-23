import { Component, Input, Output, EventEmitter } from '@angular/core';
import { AdministratorNotificationsComponent } from '../administrator-notifications/administrator-notifications.component';
import { Admin, NotificationSettings } from '../api.service';

@Component({
  selector: 'app-administrators',
  imports: [AdministratorNotificationsComponent],
  templateUrl: './administrators.component.html',
})
export class AdministratorsComponent {
  @Input() admins: Admin[] = [];
  @Input() saving = false;
  @Output() saveNotifications = new EventEmitter<{
    admin: Admin;
    settings: NotificationSettings;
  }>();
  @Output() toggleAccess = new EventEmitter<Admin>();
}
