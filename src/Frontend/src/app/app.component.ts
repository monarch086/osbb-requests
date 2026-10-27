import { AsyncPipe } from '@angular/common';
import type { OnInit } from '@angular/core';
import { Component, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink, RouterOutlet } from '@angular/router';
import type { Subscription } from 'rxjs';

import { AuthService } from './auth.service';
import { AuthPageComponent } from './auth-page.component';
import { errorMessage } from './error-message';
import { WorkspaceComponent } from './workspace/workspace.component';

@Component({
  selector: 'app-root',
  imports: [AsyncPipe, AuthPageComponent, RouterLink, RouterOutlet],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  private workspaceSaving?: Subscription;
  protected ready = false;
  protected saving = false;
  protected showAuth = false;
  protected error = '';
  ngOnInit(): void {
    void this.initialize();
  }

  private async initialize(): Promise<void> {
    this.auth.user$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((user) => {
      if (!user) {
        this.saving = false;
      }
    });
    try {
      await this.auth.initialize();
    } catch (e) {
      this.error = errorMessage(e);
    } finally {
      this.ready = true;
    }
  }
  protected activateWorkspace(workspace: unknown): void {
    this.deactivateWorkspace();
    if (workspace instanceof WorkspaceComponent) {
      this.workspaceSaving = workspace.savingChange
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe((saving) => (this.saving = saving));
    }
  }
  protected deactivateWorkspace(): void {
    this.workspaceSaving?.unsubscribe();
    this.saving = false;
  }
  protected async logout(): Promise<void> {
    if (!this.saving) {
      await this.auth.signOut();
    }
  }
}
