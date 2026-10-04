import { HttpErrorResponse } from '@angular/common/http';

/**
 * Ошибка вызова API. `message` — ВСЕГДА текст, который можно показать пользователю как есть
 * (по-русски, без кодов/statusText/HTML) — UI повсюду делает `e instanceof ApiError ? e.message : …`.
 * Машинный код ответа (`{code}` или кодовый `{reason}`) лежит отдельно в `code` — по нему и
 * ветвиться, а не по тексту.
 */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    /** Человекочитаемое сообщение бэкенда (поле `message` тела ошибки), если оно есть. Нужен там,
     * где важна дословная причина отказа (очередь «Одобрение» в админке). */
    public readonly detail?: string,
    /** Машинный код ошибки бэкенда (`{code: "last_admin"}`, `{reason: "expired"}`), если есть. */
    public readonly code?: string,
    /** Тело ответа целиком — когда кроме сообщения нужны данные (например, id конфликтующей записи). */
    public readonly body?: Record<string, unknown>,
  ) {
    super(message);
  }
}

/** Коды бэкенда, которые долетают до обычного пользователя без собственного русского `message`. */
const CODE_MESSAGES: Record<string, string> = {
  last_admin: 'Вы единственный администратор семьи. Сначала удалите семью или исключите других участников.',
  confirmation_required: 'Подтвердите действие ещё раз.',
  attachment_too_large: 'Файл слишком большой.',
  attachment_limit_reached: 'К записи прикреплено максимальное число файлов.',
  unsupported_content_type: 'Этот тип файла не поддерживается. Подойдут фото (JPG, PNG, HEIC), PDF, Word или Excel.',
  indicator_conflict: 'Такой показатель в этой записи уже есть.',
  invalid_input: 'Проверьте введённые данные.',
  invalid_time_zone: 'Не удалось определить часовой пояс — обновите страницу.',
  consent_required: 'Нужно принять обновлённые условия — обновите страницу.',
  stale_version: 'Условия обновились — обновите страницу.',
  csrf_token_invalid: 'Сессия устарела — обновите страницу.',
  already_linked: 'Telegram уже привязан к аккаунту.',
  bot_unavailable: 'Привязка Telegram временно недоступна.',
  password_required: 'Сначала задайте пароль в профиле.',
  revoked: 'Приглашение отозвано.',
  expired: 'Срок действия приглашения истёк.',
  exhausted: 'Приглашение уже использовано.',
};

/** Запасной текст по HTTP-статусу — когда бэкенд не прислал ничего пригодного для показа. */
export function statusMessage(status: number): string {
  if (status === 0) return 'Нет связи с сервером. Проверьте интернет и попробуйте ещё раз.';
  if (status === 401) return 'Сессия истекла — войдите снова.';
  if (status === 403) return 'Нет доступа к этому действию.';
  if (status === 404) return 'Не найдено — возможно, это уже удалили.';
  if (status === 409) return 'Данные успели измениться. Обновите страницу и попробуйте снова.';
  if (status === 413) return 'Файл слишком большой.';
  if (status === 429) return 'Слишком много попыток. Подождите минуту и попробуйте снова.';
  if (status >= 500) return 'Сервер временно недоступен. Попробуйте чуть позже.';
  if (status >= 400) return 'Проверьте введённые данные и попробуйте снова.';
  return 'Что-то пошло не так. Попробуйте ещё раз.';
}

const CODE_LIKE = /^[a-z][a-z0-9_]*$/;
const CYRILLIC = /[а-яё]/i;

/** Текст бэкенда можно показывать, только если он русский, не HTML и не простыня (стек/прокси). */
function isShowable(text: string | undefined): text is string {
  return !!text && CYRILLIC.test(text) && !text.includes('<') && text.length <= 400;
}

export function toApiError(e: unknown): ApiError {
  if (!(e instanceof HttpErrorResponse)) return new ApiError(0, statusMessage(-1));

  const body: unknown = e.error;
  let code: string | undefined;
  let text: string | undefined;
  let detail: string | undefined;

  if (typeof body === 'string') {
    text = body.trim();
  } else if (typeof body === 'object' && body !== null) {
    const b = body as { code?: unknown; reason?: unknown; message?: unknown };
    if (typeof b.code === 'string') code = b.code;
    const reason = typeof b.reason === 'string' ? b.reason : undefined;
    // {reason: "expired"} у превью инвайта — это код, а {reason: "…"} у LLM-гейта — человеческий текст.
    if (reason && CODE_LIKE.test(reason)) code ??= reason;
    else text = reason;
    if (typeof b.message === 'string') {
      detail = b.message;
      text ??= b.message;
    }
  }

  const message = isShowable(text)
    ? text
    : (code && CODE_MESSAGES[code]) ?? statusMessage(e.status);
  return new ApiError(e.status, message, detail, code);
}
