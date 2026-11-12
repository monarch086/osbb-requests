import type { ResidentCounts, ResidentType } from '../api.models';

const fields: Record<ResidentType, keyof Omit<ResidentCounts, 'total'>> = {
  Owner: 'owners',
  FamilyMember: 'familyMembers',
  Tenant: 'tenants',
  Employee: 'employees',
};

export function changeResidentCounts(
  counts: ResidentCounts | null,
  previous: ResidentType | null,
  next: ResidentType | null,
): ResidentCounts | null {
  if (!counts) {
    return null;
  }
  const result = { ...counts };
  if (previous) {
    result[fields[previous]] = Math.max(0, result[fields[previous]] - 1);
    result.total = Math.max(0, result.total - 1);
  }
  if (next) {
    result[fields[next]]++;
    result.total++;
  }
  return result;
}
