import type { BroadcastMessage } from '../api.models';
import { WorkspaceLists } from '../workspace/workspace-lists';

export class MessagesList extends WorkspaceLists {
  messages: BroadcastMessage[] = [];
  override itemCount(): number {
    return this.messages.length;
  }
  override resetPagination(): void {
    this.pagination.reset();
    this.messages = [];
  }
}
