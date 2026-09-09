// navigator.clipboard.writeText требует secure context (https/localhost) — недоступен внутри
// некоторых WebView (в частности, старый встроенный браузер Telegram на Android) и в принципе
// отсутствует в не-secure http-контексте. document.execCommand('copy') устарел, но остаётся
// единственным рабочим fallback'ом там, где Clipboard API нет или бросает исключение.

/** true — скопировано (любым из двух путей), false — оба способа недоступны/отклонены. */
export async function copyToClipboard(text: string): Promise<boolean> {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Продолжаем в fallback — некоторые WebView заявляют API, но бросают на вызове.
    }
  }

  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  textarea.style.pointerEvents = 'none';
  document.body.appendChild(textarea);
  textarea.focus();
  textarea.select();
  try {
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    document.body.removeChild(textarea);
  }
}
