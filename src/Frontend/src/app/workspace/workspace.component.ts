import type { OnDestroy, OnInit } from '@angular/core';
import { Component, EventEmitter, inject, Output } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';

import { AdministratorsSectionComponent } from '../administrators/administrators-section.component';
import type { Admin } from '../api.models';
import { ApiService } from '../api.service';
import { AuthService } from '../auth.service';
import { errorMessage } from '../error-message';
import { MessagesSectionComponent } from '../messages/messages-section.component';
import { RequestsSectionComponent } from '../requests/requests-section.component';
import { ResidentsSectionComponent } from '../residents/residents-section.component';
import type { WorkspaceTab } from './workspace-page.models';

@Component({
  selector: 'app-workspace',
  imports: [
    RouterLink,
    RequestsSectionComponent,
    ResidentsSectionComponent,
    MessagesSectionComponent,
    AdministratorsSectionComponent,
  ],
  templateUrl: './workspace.component.html',
})
export class WorkspaceComponent implements OnInit, OnDestroy {
  @Output() readonly savingChange = new EventEmitter<boolean>();
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly subscription = new Subscription();
  private epoch = 0;
  protected me: Admin | null = null;
  protected loading = false;
  protected error = '';
  protected tab: WorkspaceTab = 'requests';
  saving = false;

  ngOnInit(): void {
    this.subscription.add(
      this.route.paramMap.subscribe((params) => {
        const value = params.get('tab');
        const tab = value === 'administrators' ? 'admins' : value;
        if (tab !== 'requests' && tab !== 'residents' && tab !== 'messages' && tab !== 'admins') {
          return;
        }
        this.tab = tab;
        if (this.me && !this.me.isSuperAdmin && (tab === 'admins' || tab === 'messages')) {
          void this.router.navigateByUrl('/requests', { replaceUrl: true });
        }
      }),
    );
    this.subscription.add(
      this.auth.user$.subscribe((user) => {
        this.epoch++;
        this.me = null;
        this.loading = false;
        this.error = '';
        this.setSaving(false);
        if (user) {
          void this.workspace();
        }
      }),
    );
  }
  ngOnDestroy(): void {
    this.epoch++;
    this.subscription.unsubscribe();
  }
  protected async workspace(): Promise<void> {
    const epoch = this.epoch;
    this.loading = true;
    this.error = '';
    try {
      const user = await this.api.getCurrentAdmin();
      if (epoch !== this.epoch) {
        return;
      }
      this.me = user;
      if (!user.isSuperAdmin && (this.tab === 'admins' || this.tab === 'messages')) {
        await this.router.navigateByUrl('/requests', { replaceUrl: true });
      }
    } catch (error) {
      if (epoch === this.epoch) {
        this.error = errorMessage(error);
      }
    } finally {
      if (epoch === this.epoch) {
        this.loading = false;
      }
    }
  }
  protected setSaving(value: boolean): void {
    this.saving = value;
    this.savingChange.emit(value);
  }
}
