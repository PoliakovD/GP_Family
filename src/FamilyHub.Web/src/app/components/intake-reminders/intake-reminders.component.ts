import { Component, OnInit, inject, output, signal } from '@angular/core';
import { ApiError, ApiService } from '../../services/api.service';
import { IntakeStateService } from '../../services/intake-state.service';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { ToastService } from '../../shared/toast/toast.service';
import { ReminderSettings, WatchingEntry } from '../../models/types';
import { fromTimeInput, toTimeInput } from '../../shared/util/intake-labels';

const DEFAULT_QUIET_FROM = '23:00';
const DEFAULT_QUIET_TO = '07:00';

/**
 * Настройки напоминаний о приёме на уровне человека (макет «Screen - Medication schedule», «Настройки
 * напоминаний»): кто узнает о моих пропусках, за кем слежу я и тихие часы. Повтор напоминания, порог
 * пропуска и запас — на самом курсе (форма курса), потому что у разных лекарств они разные. Каждое
 * переключение сохраняется сразу.
 */
@Component({
  selector: 'app-intake-reminders',
  imports: [AvatarComponent],
  templateUrl: './intake-reminders.component.html',
  styleUrl: './intake-reminders.component.scss',
})
export class IntakeRemindersComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly intake = inject(IntakeStateService);

  readonly closed = output<void>();

  readonly settings = signal<ReminderSettings | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly quietFrom = signal(DEFAULT_QUIET_FROM);
  readonly quietTo = signal(DEFAULT_QUIET_TO);
  readonly quietOn = signal(false);

  protected person(id: string, name: string): { key: string; firstName: string } {
    return { key: id, firstName: name };
  }

  ngOnInit(): void {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const s = await this.api.getReminderSettings();
      this.settings.set(s);
      this.quietOn.set(s.quietHoursFrom !== null && s.quietHoursTo !== null);
      this.quietFrom.set(toTimeInput(s.quietHoursFrom) || DEFAULT_QUIET_FROM);
      this.quietTo.set(toTimeInput(s.quietHoursTo) || DEFAULT_QUIET_TO);
    } catch {
      this.loadError.set('Не удалось загрузить настройки. Проверьте соединение и попробуйте ещё раз.');
    } finally {
      this.loading.set(false);
    }
  }

  protected async toggleMyWatcher(userId: string, enabled: boolean): Promise<void> {
    const current = this.settings();
    if (!current) return;
    const next = current.myWatchers.map((w) => (w.userId === userId ? { ...w, enabled } : w));
    this.settings.set({ ...current, myWatchers: next });
    await this.save(() => this.api.setMyWatchers(next.filter((w) => w.enabled).map((w) => w.userId)));
  }

  protected async setWatching(entry: WatchingEntry, changes: { notifyMissed?: boolean; receiveReminders?: boolean }): Promise<void> {
    const current = this.settings();
    if (!current) return;
    const updated: WatchingEntry = { ...entry, ...changes };
    updated.isWatching = updated.kind === 'user' ? true : updated.notifyMissed || updated.receiveReminders;
    this.settings.set({
      ...current,
      watching: current.watching.map((w) => (w.kind === entry.kind && w.id === entry.id ? updated : w)),
    });
    await this.save(() => this.api.setWatching(entry.kind, entry.id, updated.notifyMissed, updated.receiveReminders));
  }

  /** «Следить» за подопечным: включает и пропуски, и напоминания; выключение снимает оба. */
  protected toggleWatch(entry: WatchingEntry, on: boolean): Promise<void> {
    return this.setWatching(entry, { notifyMissed: on, receiveReminders: on });
  }

  protected async setQuiet(on: boolean): Promise<void> {
    this.quietOn.set(on);
    await this.saveQuiet();
  }

  protected async saveQuiet(): Promise<void> {
    const on = this.quietOn();
    await this.save(() => this.api.setQuietHours(on ? fromTimeInput(this.quietFrom()) : null, on ? fromTimeInput(this.quietTo()) : null));
  }

  private async save(action: () => Promise<void>): Promise<void> {
    try {
      await action();
      this.intake.changed();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.message ? e.message : 'Не удалось сохранить настройки');
      await this.load();
    }
  }
}
