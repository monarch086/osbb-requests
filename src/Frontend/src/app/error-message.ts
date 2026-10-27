import { HttpErrorResponse } from '@angular/common/http';

export function errorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 401) {
      return 'Сеанс завершено. Увійдіть ще раз.';
    }
    if (error.status === 404 || error.status === 403) {
      return 'Немає доступу до цієї дії.';
    }
    if (error.status === 400) {
      return 'Перевірте налаштування та спробуйте ще раз.';
    }
    return 'Не вдалося зв’язатися із сервером. Спробуйте ще раз.';
  }
  return error instanceof UserFacingError ? error.message : 'Сталася помилка. Спробуйте ще раз.';
}

export class UserFacingError extends Error {}
