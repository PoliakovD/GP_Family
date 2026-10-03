import { computed, signal } from '@angular/core';

/** Сколько ждать до «Отправить код ещё раз» — чтобы человек не сжёг лимит (3 кода в час) за минуту. */
export const RESEND_COOLDOWN_SECONDS = 60;

/**
 * Обратный отсчёт для кнопки повторной отправки кода из письма. Раньше повторной отправки не
 * было вовсе: единственный путь — «Изменить данные» и отправить форму заново, что молча тратило
 * один из трёх кодов в час.
 */
export class ResendCooldown {
  readonly secondsLeft = signal(0);
  readonly ready = computed(() => this.secondsLeft() === 0);
  /** «0:45» для подписи кнопки. */
  readonly label = computed(() => {
    const s = this.secondsLeft();
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  });

  private timer: ReturnType<typeof setInterval> | null = null;

  start(seconds = RESEND_COOLDOWN_SECONDS): void {
    this.stop();
    this.secondsLeft.set(seconds);
    this.timer = setInterval(() => {
      const next = this.secondsLeft() - 1;
      this.secondsLeft.set(Math.max(0, next));
      if (next <= 0) this.stop();
    }, 1000);
  }

  stop(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
  }
}
