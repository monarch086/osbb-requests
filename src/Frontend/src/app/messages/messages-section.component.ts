import { Component } from '@angular/core';

import type { MessageChange } from '../api.models';
import { PaginationComponent } from '../pagination/pagination.component';
import { WorkspaceSection } from '../workspace/workspace-section';
import { MessagesComponent } from './messages.component';
import { MessagesList } from './messages-list';

@Component({
  selector: 'app-messages-section',
  imports: [MessagesComponent, PaginationComponent],
  templateUrl: './messages-section.component.html',
})
export class MessagesSectionComponent extends WorkspaceSection {
  protected override readonly lists = new MessagesList();
  protected sentMessageId: string | null = null;
  protected override async loadItems(
    index: number,
    cursor: string | null,
    isCurrent: () => boolean,
  ): Promise<void> {
    const page = await this.api.getMessages(cursor);
    if (isCurrent()) {
      this.lists.messages = page.items;
      this.lists.pagination.accept(index, cursor, page.cursor);
    }
  }
  protected async sendMessage(change: MessageChange): Promise<void> {
    if (
      !this.me.effectivePermissions.messages ||
      !this.me.isActive ||
      this.loading ||
      this.saving
    ) {
      return;
    }
    const epoch = this.epoch;
    const writeState = { sent: false };
    await this.write(async () => {
      const result = await this.api.sendMessage(change);
      if (epoch !== this.epoch) {
        return;
      }
      this.sentMessageId = result.id;
      this.notice = 'Повідомлення передано на розсилку мешканцям у Telegram.';
      writeState.sent = true;
    });
    if (writeState.sent && epoch === this.epoch) {
      this.lists.resetPagination();
      await this.load();
    }
  }
}
