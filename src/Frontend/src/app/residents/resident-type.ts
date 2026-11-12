import type { ResidentType } from '../api.models';

export function residentTypeLabel(type: ResidentType | null | undefined): string {
  switch (type) {
    case 'Owner':
      return 'Власник';
    case 'Tenant':
      return 'Орендар';
    case 'Employee':
      return 'Працівник ОСББ';
    case 'FamilyMember':
    case null:
    case undefined:
      return 'Член родини';
  }
}
