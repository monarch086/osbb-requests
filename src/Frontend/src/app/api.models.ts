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
  notifyNewKeyOrders: boolean;
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
export interface ResidentCounts {
  total: number;
  owners: number;
  familyMembers: number;
  tenants: number;
  employees: number;
}
export interface ResidentDirectoryPage<T> {
  items: T[];
  pageNumber: number;
  pageSize: number;
  total: number;
  counts: ResidentCounts;
  startCursor?: string | null;
  nextCursor?: string | null;
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
export type ResidentListRow = Pick<
  RegisteredResident,
  | 'id'
  | 'firstName'
  | 'lastName'
  | 'residentType'
  | 'buildingNumber'
  | 'apartmentNumber'
  | 'personalDataDeleted'
>;
export interface RegisteredResidentDetails {
  resident: RegisteredResident;
  approval: RegistrationApproval | null;
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

export interface KeyOrderItem {
  type: string;
  phone: string;
  serialNumber?: string | null;
}

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
  keyItems?: KeyOrderItem[] | null;
}
export interface ResidentKeyEntry {
  name: string;
  phone: string;
  serialNumber: string | null;
  requestId: number;
  keyIndex: number;
}
export interface ResidentKeyPage {
  items: ResidentKeyEntry[];
  pageNumber: number;
  pageSize: number;
  total: number;
}

export interface DailyStatistic {
  date: string;
  endpoint: string;
  requests: number;
  readUnits: number;
  writeUnits: number;
  durationMs: number;
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
  /** @deprecated Use keyResidents; retained temporarily for older API versions. */
  keyResidentNames?: string[][] | null;
  keyResidents?: KeyResident[][] | null;
  version: number;
}
export interface KeyResident {
  name: string;
  buildingNumber: string | null;
  apartmentNumber: string | null;
}
export interface Page<T> {
  items: T[];
  cursor: string | null;
}
export interface TicketPage extends Page<ManagedTicket> {
  pageNumber: number;
  pageSize: number;
  counts: { total: number; newCount: number; progressCount: number } | null;
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
