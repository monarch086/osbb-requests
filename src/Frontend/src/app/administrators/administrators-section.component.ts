import { Component } from '@angular/core';

import type { Admin, NotificationSettings } from '../api.models';
import { PaginationComponent } from '../pagination/pagination.component';
import { WorkspaceSection } from '../workspace/workspace-section';
import { AdministratorsComponent } from './administrators.component';
import { AdministratorsList } from './administrators-list';

@Component({
  selector: 'app-administrators-section',
  imports: [AdministratorsComponent, PaginationComponent],
  templateUrl: './administrators-section.component.html',
})
export class AdministratorsSectionComponent extends WorkspaceSection {
  protected override readonly lists = new AdministratorsList();
  protected override async loadItems(
    index: number,
    cursor: string | null,
    isCurrent: () => boolean,
  ): Promise<void> {
    const page = await this.api.getAdministrators(cursor);
    if (isCurrent()) {
      this.lists.admins = page.items;
      this.lists.pagination.accept(index, cursor, page.cursor);
    }
  }
  protected async saveNotificationSettings(
    admin: Admin,
    settings: NotificationSettings,
  ): Promise<void> {
    if (!this.me.isSuperAdmin) {
      return;
    }
    await this.write(async () => {
      await this.api.updateNotificationSettings(admin, settings);
      Object.assign(admin, settings, { version: admin.version + 1 });
      if (this.me.userId === admin.userId) {
        Object.assign(this.me, settings, { version: admin.version });
      }
      this.notice = 'Налаштування сповіщень збережено.';
    });
  }
  protected async setAccess(user: Admin): Promise<void> {
    if (!this.me.isSuperAdmin || user.isSuperAdmin) {
      return;
    }
    await this.write(async () => {
      await this.api.updateAdminAccess(user);
      user.isActive = !user.isActive;
      user.version++;
      this.notice = 'Доступ адміністратора оновлено.';
    });
  }
}
