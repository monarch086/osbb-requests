import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Admin, NotificationSettings } from '../api.service';

@Component({
  selector: 'app-administrator-notifications',
  imports: [FormsModule],
  templateUrl: './administrator-notifications.component.html',
})
export class AdministratorNotificationsComponent implements OnChanges {
  @Input({ required: true }) admin!: Admin;
  @Input() saving = false;
  @Output() saved = new EventEmitter<NotificationSettings>();
  settings: NotificationSettings = {
    telegramChatId: '',
    notifyNewResidents: false,
    notifyNewRequests: false,
    notifyNewAdministrators: false,
  };
  ngOnChanges(changes: SimpleChanges): void {
    if (changes['admin']) {
      this.settings = {
        telegramChatId: this.admin.telegramChatId ?? '',
        notifyNewResidents: this.admin.notifyNewResidents,
        notifyNewRequests: this.admin.notifyNewRequests,
        notifyNewAdministrators: this.admin.notifyNewAdministrators,
      };
    }
  }
  get missingChat(): boolean {
    return (
      !this.settings.telegramChatId?.trim() &&
      (this.settings.notifyNewResidents ||
        this.settings.notifyNewRequests ||
        this.settings.notifyNewAdministrators)
    );
  }
  submit(): void {
    if (!this.saving && !this.missingChat) this.saved.emit({ ...this.settings });
  }
}
