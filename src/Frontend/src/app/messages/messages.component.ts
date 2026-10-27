import type { OnChanges } from '@angular/core';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

import type { BroadcastMessage, MessageChange } from '../api.service';
import { formatDate } from '../shared/ticket-presentation';

@Component({
  selector: 'app-messages',
  imports: [FormsModule],
  templateUrl: './messages.component.html',
  styleUrl: './messages.component.scss',
})
export class MessagesComponent implements OnChanges {
  @Input() messages: BroadcastMessage[] = [];
  @Input() loading = false;
  @Input() saving = false;
  @Input() sentMessageId: string | null = null;
  @Output() readonly sendMessage = new EventEmitter<MessageChange>();
  protected text = '';
  private attempt: MessageChange | null = null;
  protected readonly date = formatDate;

  ngOnChanges(): void {
    if (this.attempt && this.sentMessageId === this.attempt.id) {
      this.text = '';
      this.attempt = null;
    }
  }
  protected send(): void {
    if (this.loading || this.saving || !this.text.trim() || this.text.length > 4096) {
      return;
    }
    const text = this.text.trim();
    if (this.attempt?.text !== text) {
      this.attempt = { id: crypto.randomUUID(), text };
    }
    this.sendMessage.emit(this.attempt);
  }
}
