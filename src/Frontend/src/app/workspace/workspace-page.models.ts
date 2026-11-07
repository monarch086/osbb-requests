import type { Admin, BroadcastMessage, ManagedTicket, Page, Registration } from '../api.models';

export type WorkspaceTab = 'requests' | 'residents' | 'messages' | 'admins';
export type WorkspacePage =
  | { kind: 'requests'; page: Page<ManagedTicket> }
  | { kind: 'residents'; page: Page<Registration> }
  | { kind: 'messages'; page: Page<BroadcastMessage> }
  | { kind: 'admins'; page: Page<Admin> };
