import { TelegramService } from '../../services/telegram.service';

/**
 * Поделиться ссылкой: в Telegram — нативный шаринг (пользователь сам выбирает чат; чужие ID мы не
 * запрашиваем), иначе системный `navigator.share`. Возвращает false, если системного шаринга нет и
 * вызывающему нужно предложить копирование. Отмена диалога пользователем — не ошибка (true).
 */
export async function shareLink(tg: TelegramService, url: string, title: string, text: string): Promise<boolean> {
  if (tg.isInsideTelegram()) {
    tg.openTelegramLink(`https://t.me/share/url?url=${encodeURIComponent(url)}&text=${encodeURIComponent(text)}`);
    return true;
  }
  if (!navigator.share) return false;
  try {
    await navigator.share({ title, text, url });
  } catch {
    // пользователь закрыл системный диалог — игнорируем
  }
  return true;
}
