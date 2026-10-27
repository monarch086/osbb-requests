import type { CanDeactivateFn, Routes } from '@angular/router';

import { WorkspaceComponent } from './workspace/workspace.component';

const canLeaveWorkspace: CanDeactivateFn<WorkspaceComponent> = (workspace) => !workspace.saving;

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'requests' },
  ...[
    { path: 'requests', tab: 'requests' },
    { path: 'residents', tab: 'residents' },
    { path: 'messages', tab: 'messages' },
    { path: 'administrators', tab: 'admins' },
  ].map(({ path, tab }) => ({
    path,
    component: WorkspaceComponent,
    data: { tab },
    canDeactivate: [canLeaveWorkspace],
  })),
  { path: '**', redirectTo: 'requests' },
];
