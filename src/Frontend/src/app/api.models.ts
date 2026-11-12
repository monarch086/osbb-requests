export interface MessageChange {
  id: string;
  text: string;
}
export interface BroadcastMessage extends MessageChange {
  createdBy: string;
  createdById: string;
  createdAt: string;
}
export interface NotificationSettings {
  telegramChatId: string | null;
  notifyNewResidents: boolean;
  notifyNewRequests: boolean;
  notifyNewAdministrators: boolean;
}
export interface Admin extends NotificationSettings {
  userId: string;
  email: string;
  displayName: string;
  isActive: boolean;
  isSuperAdmin: boolean;
  version: number;
  effectivePermissions: PagePermissions;
}
export interface PagePermissions {
  complexRequests: boolean;
  keyOrders: boolean;
  registeredResidents: boolean;
  pendingResidents: boolean;
  messages: boolean;
}
export type ResidentType = 'Owner' | 'FamilyMember' | 'Tenant' | 'Employee';
export interface ResidentDirectoryPage<T> {
  items: T[];
  pageNumber: number;
  pageSize: number;
  total: number;
}
export interface Registration {
  telegramChatId: string;
  residentType?: ResidentType;
  typeChangeCount?: number;
  id: string;
  firstName: string;
  lastName: string;
  phone: string;
  email?: string | null;
  buildingNumber?: string | null;
  apartmentNumber?: string | null;
  consentAt?: string | null;
  updatedAt: string;
  version: number;
}
export interface RegisteredResident {
  telegramChatId: string;
  residentType?: ResidentType;
  typeChangeCount?: number;
  id: string;
  firstName: string | null;
  lastName: string | null;
  phone: string | null;
  email?: string | null;
  buildingNumber?: string | null;
  apartmentNumber?: string | null;
  personalDataDeleted?: boolean;
  registeredAt: string;
  version: number;
  consentAt?: string | null;
}
export interface RegistrationApproval {
  actor: string;
  createdAt: string | null;
  automatic: boolean;
}
export interface ResidentDataChange {
  firstName: string;
  lastName: string;
  residentType: ResidentType;
}
export interface ResidentTypeHistory {
  previousType: ResidentType;
  newType: ResidentType;
  firstNameChanged?: boolean;
  lastNameChanged?: boolean;
  actor: string;
  createdAt: string;
}
export type TicketStatus =
  'New' | 'InProgress' | 'AwaitingClarification' | 'Completed' | 'Rejected';
export type TicketPriority = 'Low' | 'Normal' | 'High' | 'Urgent';

export interface Ticket {
  id: number;
  ownerId: number;
  scope: 'Complex' | 'Building' | null;
  buildingNumber: string | null;
  floor: number | null;
  entrance: number | null;
  category: string;
  location: string;
  description: string;
  createdAt: string;
  status: TicketStatus;
  visibility: string;
  resolution: string | null;
  assignedTo: string | null;
  priority: TicketPriority;
  plannedDate: string | null;
}
export interface RequestAuthor {
  phone?: string | null;
  personalDataDeleted?: boolean;
  firstName: string | null;
  lastName: string | null;
  buildingNumber: string | null;
  apartmentNumber: string | null;
}
export interface ManagedTicket {
  request: Ticket;
  author?: RequestAuthor | null;
  version: number;
}
export interface Page<T> {
  items: T[];
  cursor: string | null;
}
export interface TicketPage extends Page<ManagedTicket> {
  pageNumber: number;
  pageSize: number;
  counts: { total: number; newCount: number; progressCount: number };
}

export interface Assignee {
  userId: string;
  email: string;
}

export interface TicketFilters {
  section?: string;
  target: string;
  floor: string;
  status: TicketStatus[];
  category: string;
  search: string;
  from: string;
  to: string;
}

export interface TicketHistory {
  items: Record<string, string>[];
}
