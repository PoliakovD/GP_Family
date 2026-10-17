import { Injectable, TemplateRef, computed, signal } from '@angular/core';
import type { IconNode as LucideIconData } from 'lucide';
import type { Tone } from '../shared/ui/tone';

/** Страница поверх раздела: форма добавления или статья справочника, открытая на десктопе. */
export interface ContentPage {
  /** Кто открыл (панель-обёртка) — по нему закрывают и обновляют. */
  owner: object;
  title: string;
  kicker?: string | null;
  /** Подпись ссылки «‹ Назад к …» — раздел, в который вернёмся. */
  backLabel: string;
  icon?: LucideIconData | null;
  tone?: Tone;
  /** Ширина колонки страницы: форма — 760px, широкая таблица (прививки) — больше. */
  maxWidth?: string;
  template: TemplateRef<unknown>;
  /** «Назад», Escape, системная «назад» — владелец решает, закрываться ли (несохранённые данные). */
  close: () => void;
}

/**
 * Формы добавления и статьи справочника на десктопе (≥1024) открываются не панелью справа поверх
 * экрана, а страницей в колонке контента — как открытая запись анализа: «‹ Назад к разделу» →
 * заголовок → содержимое. Раздел под ней не уничтожается (каркас его только прячет), поэтому после
 * закрытия список, фильтры и прокрутка на месте. Стек — на случай справки, открытой из формы.
 * Рисует страницу каркас (app.component, `<app-content-page-host>`).
 */
@Injectable({ providedIn: 'root' })
export class ContentPageService {
  private readonly stack = signal<ContentPage[]>([]);

  readonly current = computed<ContentPage | null>(() => this.stack().at(-1) ?? null);

  open(page: ContentPage): void {
    this.stack.update((s) => [...s.filter((p) => p.owner !== page.owner), page]);
  }

  update(owner: object, patch: Partial<Omit<ContentPage, 'owner'>>): void {
    this.stack.update((s) => s.map((p) => (p.owner === owner ? { ...p, ...patch } : p)));
  }

  close(owner: object): void {
    this.stack.update((s) => s.filter((p) => p.owner !== owner));
  }

  isOpen(owner: object): boolean {
    return this.stack().some((p) => p.owner === owner);
  }
}
