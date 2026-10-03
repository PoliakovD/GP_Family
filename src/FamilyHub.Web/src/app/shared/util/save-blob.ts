/**
 * Сохранение файла, полученного через HttpClient (PDF отчёта, сертификат, архив данных).
 *
 * Раньше в каждом месте был свой синтетический `<a download>`: где-то `revokeObjectURL` сразу после
 * click() (в части браузеров загрузка обрывалась), а в Telegram WebView `download` у blob-ссылки
 * игнорируется — кнопка «Скачать» молча ничего не делала.
 *
 * Возвращает текст для подсказки пользователю (или null, если сказать нечего).
 */
export async function saveBlob(blob: Blob, filename: string, insideTelegram: boolean): Promise<string | null> {
  if (insideTelegram) {
    // В Telegram скачать blob нельзя; системное «Поделиться» с файлом умеет сохранить его.
    const file = new File([blob], filename, { type: blob.type || 'application/octet-stream' });
    const nav = navigator as Navigator & { canShare?: (data: ShareData) => boolean };
    if (nav.share && nav.canShare?.({ files: [file] })) {
      try {
        await nav.share({ files: [file], title: filename });
        return null;
      } catch {
        return null; // пользователь закрыл меню «Поделиться» — это не ошибка
      }
    }
    return 'В Telegram файл не скачивается — откройте FamilyHub в браузере, чтобы сохранить его.';
  }

  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.rel = 'noopener';
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  a.remove();
  // Не отзываем сразу: загрузка большого файла стартует асинхронно.
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
  return 'Файл скачивается — он появится в «Загрузках».';
}
