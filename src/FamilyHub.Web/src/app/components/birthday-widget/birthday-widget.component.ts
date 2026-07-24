import { Component, OnInit, effect, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import type { Birthday } from '../../models/types';
import {
  birthdayMetaLabel,
  birthdayUrgencyLabel,
  daysUntilNextBirthday,
} from '../../shared/util/birthday-date';

const TOP_N = 3;

/**
 * Виджет «ближайшие дни рождения» на Главной (редизайн навигации) — read-only срез по ВСЕМ
 * активным семьям пользователя, в отличие от BirthdaysPanelComponent (полное CRUD по одной
 * семье, семейный саб-таб / отдельная страница /birthdays). Датовая арифметика общая —
 * shared/util/birthday-date.ts, не дублируем.
 */
@Component({
  selector: 'app-birthday-widget',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './birthday-widget.component.html',
})
export class BirthdayWidgetComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly state = inject(FamilyStateService);

  items: Birthday[] = [];
  loading = true;

  // undefined — ещё ни разу не загружали для текущего набора семей.
  private loadedFamilyIds: string | undefined = undefined;

  constructor() {
    // FamilyStateService.refresh() (шелл) может завершиться уже ПОСЛЕ монтирования виджета —
    // реагируем на появление/смену набора активных семей, а не только на ngOnInit.
    effect(() => {
      const key = this.state.activeFamilies().map((f) => f.id).sort().join(',');
      if (key === this.loadedFamilyIds) return;
      void this.refresh();
    });
  }

  ngOnInit(): void {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    const families = this.state.activeFamilies();
    this.loadedFamilyIds = families.map((f) => f.id).sort().join(',');
    if (families.length === 0) {
      this.items = [];
      this.loading = false;
      return;
    }

    this.loading = true;
    try {
      const perFamily = await Promise.all(families.map((f) => this.api.getBirthdays(f.id)));
      this.items = perFamily.flat();
    } catch {
      // Виджет вспомогательный — при сбое просто не показываем ничего, без баннера ошибки
      // поверх Главной (полный список всё равно доступен на /birthdays).
      this.items = [];
    } finally {
      this.loading = false;
    }
  }

  urgencyLabel(item: Birthday): string {
    return birthdayUrgencyLabel(item.personName, item.date);
  }

  metaLabel(item: Birthday): string {
    return birthdayMetaLabel(item.date);
  }

  /** Топ-N ближайших по всем семьям, отсортированные по дням до наступления. */
  get upcoming(): Birthday[] {
    return this.items
      .slice()
      .sort((a, b) => daysUntilNextBirthday(a.date) - daysUntilNextBirthday(b.date))
      .slice(0, TOP_N);
  }
}
