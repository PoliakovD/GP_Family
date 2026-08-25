import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AdminApiService } from './admin-api.service';

/**
 * Гейт для /admin/* (ADR-0009) — отдельная сессия от обычной PWA/Telegram (см. authGuard),
 * проверяется собственным GET /api/admin/session (200 при валидной cookie, иначе 401).
 * Не кэширует результат сигналом (в отличие от authGuard/auth.me()) — сессия админки короткая
 * и её проверка достаточно дешёвая, чтобы не заводить отдельное состояние ради неё.
 */
export const adminGuard: CanActivateFn = async () => {
  const api = inject(AdminApiService);
  const router = inject(Router);

  try {
    await api.checkSession();
    return true;
  } catch {
    return router.createUrlTree(['/admin/login']);
  }
};
