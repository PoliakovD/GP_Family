import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { ApiService } from '../../../services/api.service';
import { AuthService } from '../../../services/auth.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { HealthShareCategory, HealthShareGrantDto } from '../../../models/types';
import { runBusy } from '../settings-task';

import { saveBlob } from '../../../shared/util/save-blob';
import { TelegramService } from '../../../services/telegram.service';
/** Вкладка «Данные»: политика конфиденциальности, выгрузка данных, доступ к здоровью (ADR-0017),
 * удаление аккаунта (152-ФЗ). */
@Component({
    selector: 'app-settings-data',
    imports: [FormsModule, RouterLink],
    templateUrl: './settings-data.component.html',
    styleUrl: './settings-data.component.scss',
})
export class SettingsDataComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly tgForExport = inject(TelegramService);

  readonly busy = signal(false);
  readonly deleteConfirmVisible = signal(false);
  deleteConfirmText = '';

  /** «удалить», «Удалить », «УДАЛИТЬ» — всё подходит (раньше строчные буквы молча не принимались). */
  get deleteConfirmed(): boolean {
    return this.deleteConfirmText.trim().toUpperCase() === 'УДАЛИТЬ';
  }

  /** «Кто видит моё здоровье» (ADR-0017) — гранты доступа per-категория к дневнику/приёму/
   * прививкам. Полная матрица кандидатов (активные члены моих семей), не только тех, кому уже
   * что-то открыто — тот же sparse-preference приём, что и у оповещений выше на этой вкладке. */
  protected readonly Category = HealthShareCategory;
  readonly shares = signal<HealthShareGrantDto[]>([]);
  readonly sharesLoading = signal(true);
  /** `viewerUserId:bit` переключателя, который сейчас сохраняется — остальные блокируются, чтобы
   * не наложить два PUT на одного и того же человека. */
  readonly shareBusyKey = signal<string | null>(null);

  ngOnInit(): void {
    void this.loadShares();
  }

  private async loadShares(): Promise<void> {
    this.sharesLoading.set(true);
    try {
      this.shares.set(await this.api.getMyHealthShares());
    } catch {
      this.toast.error('Не удалось загрузить список доступа к здоровью.');
    } finally {
      this.sharesLoading.set(false);
    }
  }

  protected hasCategory(grant: HealthShareGrantDto, bit: number): boolean {
    return (grant.categories & bit) === bit;
  }

  /** Оптимистичное переключение одной категории одного человека — при ошибке откатываем. */
  protected async toggleCategory(grant: HealthShareGrantDto, bit: number): Promise<void> {
    if (this.shareBusyKey() !== null) return;
    const prev = grant.categories;
    const next = this.hasCategory(grant, bit) ? prev & ~bit : prev | bit;

    this.shareBusyKey.set(`${grant.viewerUserId}:${bit}`);
    this.shares.update((list) =>
      list.map((g) => (g.viewerUserId === grant.viewerUserId ? { ...g, categories: next } : g)));
    try {
      await this.api.setHealthShare(grant.viewerUserId, next);
    } catch {
      this.toast.error('Не удалось изменить доступ.');
      this.shares.update((list) =>
        list.map((g) => (g.viewerUserId === grant.viewerUserId ? { ...g, categories: prev } : g)));
    } finally {
      this.shareBusyKey.set(null);
    }
  }

  /**
   * Через HttpClient (не <a href download>) — обычная навигация браузера не идёт через
   * authInterceptor и не получает Telegram-заголовок авторизации; в Mini App это раньше
   * приводило к 401 и скачиванию файла с текстом ошибки вместо архива.
   */
  async exportData(): Promise<void> {
    await runBusy(this.busy, this.toast, async () => {
      const blob = await this.auth.exportAccountData();
      const hint = await saveBlob(blob, 'familyhub-export.zip', this.tgForExport.isInsideTelegram());
      if (hint) this.toast.info(hint);
    });
  }

  async deleteAccount(): Promise<void> {
    if (!this.deleteConfirmed) return;
    await runBusy(this.busy, this.toast, async () => {
      try {
        await this.auth.deleteAccount();
        this.toast.success('Аккаунт и все данные удалены');
        await this.router.navigate(['/login']);
      } catch (e) {
        if (e instanceof HttpErrorResponse && e.error?.code === 'last_admin') {
          this.toast.error('Вы единственный администратор семьи с участниками. Сначала исключите участников или удалите семью (раздел «Семья»).');
          return;
        }
        throw e;
      }
    });
  }
}
