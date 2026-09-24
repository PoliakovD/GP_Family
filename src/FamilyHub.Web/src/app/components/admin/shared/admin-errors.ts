import { ApiError } from '../../../services/api.service';

/**
 * Единое правило показа ошибок в админке:
 *  - ошибка ЗАГРУЗКИ страницы/карточки — inline `<div class="alert-danger">` на месте данных
 *    (пользователь видит, что именно не загрузилось, и оно не исчезает через 6 секунд);
 *  - ошибка ДЕЙСТВИЯ (сохранить, запустить, удалить) — `toast.error(...)`, страница при этом
 *    остаётся рабочей.
 *
 * Раньше Обзор/Хранилище/Система/Ключи показывали inline-блок, а остальные страницы — только
 * тост, поэтому упавшая загрузка там выглядела как пустая страница.
 */

/** Машинный код ошибки бэкенда (`{ code: "..." }` → ApiError.message, см. AdminApiService.toApiError),
 * либо null, если ошибка не от API. */
export function apiErrorCode(e: unknown): string | null {
  return e instanceof ApiError ? e.message : null;
}
