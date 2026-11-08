import type { ResidentType } from '../api.models';

export function residentTypeLabel(type: ResidentType | null | undefined): string {
  switch (type) {
    case 'Owner':
      return 'Власник';
    case 'Tenant':
      return 'Орендар';
    case 'FamilyMember':
    case null:
    case undefined:
      return 'Член родини';
  }
}
