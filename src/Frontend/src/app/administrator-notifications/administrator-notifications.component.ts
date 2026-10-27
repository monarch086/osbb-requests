import type { OnChanges, SimpleChanges } from '@angular/core';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

import type { Admin, NotificationSettings } from '../api.models';

@Component({
  selector: 'app-administrator-notifications',
  imports: [FormsModule],
  templateUrl: './administrator-notifications.component.html',
})
export class AdministratorNotificationsComponent implements OnChanges {
  @Input({ required: true }) admin!: Admin;
  @Input() saving = false;
  @Output() readonly saved = new EventEmitter<NotificationSettings>();
  protected settings: NotificationSettings = {
    telegramChatId: '',
    notifyNewResidents: false,
    notifyNewRequests: false,
    notifyNewAdministrators: false,
  };
  protected get missingChat(): boolean {
    return (
      !this.settings.telegramChatId?.trim() &&
      (this.settings.notifyNewResidents ||
        this.settings.notifyNewRequests ||
        this.settings.notifyNewAdministrators)
    );
  }
  ngOnChanges(changes: Partial<SimpleChanges>): void {
    if (changes['admin']) {
      this.settings = {
        telegramChatId: this.admin.telegramChatId ?? '',
        notifyNewResidents: this.admin.notifyNewResidents,
        notifyNewRequests: this.admin.notifyNewRequests,
        notifyNewAdministrators: this.admin.notifyNewAdministrators,
      };
    }
  }
  protected submit(): void {
    if (!this.saving && !this.missingChat) {
      this.saved.emit({ ...this.settings });
    }
  }
}
