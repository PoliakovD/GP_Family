/**
 * `?returnUrl=` для /login: куда вернуть человека после входа (истёкшая сессия, диплинк из пуша).
 * Принимаем только относительный путь внутри приложения — иначе это open redirect
 * (`//evil.example`, `https://…`). Страницы входа/регистрации сами по себе не цель возврата.
 */
export function safeReturnUrl(raw: string | null | undefined): string | null {
  if (!raw) return null;
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return null;
  if (/^\/(login|telegram-bind|consent)(\/|\?|$)/.test(raw)) return null;
  return raw;
}
