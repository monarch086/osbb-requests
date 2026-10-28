import { inject } from '@angular/core';
import type { CanActivateFn, CanDeactivateFn, Routes, UrlMatcher } from '@angular/router';
import { ChildrenOutletContexts } from '@angular/router';

import { WorkspaceComponent } from './workspace/workspace.component';

const canLeaveWorkspace: CanDeactivateFn<WorkspaceComponent> = (workspace) => !workspace.saving;
const canSwitchTab: CanActivateFn = () => {
  const outlet = inject(ChildrenOutletContexts).getContext('primary')?.outlet;
  return (
    !outlet?.isActivated ||
    !(outlet.component instanceof WorkspaceComponent) ||
    !outlet.component.saving
  );
};
const workspaceMatcher: UrlMatcher = (segments) => {
  if (
    segments.length !== 1 ||
    !['requests', 'residents', 'messages', 'administrators'].includes(segments[0].path)
  ) {
    return null;
  }
  return { consumed: segments, posParams: { tab: segments[0] } };
};

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'requests' },
  {
    matcher: workspaceMatcher,
    component: WorkspaceComponent,
    canActivate: [canSwitchTab],
    canDeactivate: [canLeaveWorkspace],
    runGuardsAndResolvers: 'always',
  },
  { path: '**', redirectTo: 'requests' },
];
