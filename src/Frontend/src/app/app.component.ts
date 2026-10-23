import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AsyncPipe } from '@angular/common';
import { AuthPageComponent } from './auth-page.component';
import { AuthService } from './auth.service';
import { errorMessage } from './error-message';
import { RouterLink, RouterOutlet } from '@angular/router';
import { Subscription } from 'rxjs';
import { WorkspaceComponent } from './workspace/workspace.component';

@Component({
  selector: 'app-root',
  imports: [AsyncPipe, AuthPageComponent, RouterLink, RouterOutlet],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  private workspaceSaving?: Subscription;
  ready = false;
  saving = false;
  showAuth = false;
  error = '';
  ngOnInit(): void {
    void this.initialize();
  }

  private async initialize(): Promise<void> {
    this.auth.user$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((user) => {
      if (!user) this.saving = false;
    });
    try {
      await this.auth.initialize();
    } catch (e) {
      this.error = errorMessage(e);
    } finally {
      this.ready = true;
    }
  }
  activateWorkspace(workspace: unknown): void {
    this.deactivateWorkspace();
    if (workspace instanceof WorkspaceComponent) {
      this.workspaceSaving = workspace.savingChange
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe((saving) => (this.saving = saving));
    }
  }
  deactivateWorkspace(): void {
    this.workspaceSaving?.unsubscribe();
    this.saving = false;
  }
  async logout(): Promise<void> {
    if (!this.saving) await this.auth.signOut();
  }
}
