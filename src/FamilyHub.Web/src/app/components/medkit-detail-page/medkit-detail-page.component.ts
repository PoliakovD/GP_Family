import { Component, OnDestroy, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { PageActionService } from '../../services/page-action.service';
import { ToastService } from '../../shared/toast/toast.service';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import type { Medkit } from '../../models/types';
import { BackLinkComponent } from '../../shared/back-link/back-link.component';
import { ModalComponent } from '../../shared/modal/modal.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { MedicationsPanelComponent } from '../medications-panel/medications-panel.component';
import { pluralizeRu } from '../../shared/util/pluralize';

/**
 * Экран открытой аптечки (редизайн v2.2) — как «Анализы»: отдельная страница
 * (/health/medications/:id) вместо инлайн-аккордеона (medkits-panel.component.ts, который
 * ведёт сюда, когда linkMode=true, но сам остаётся аккордеоном для family-details, где отдельной
 * деталь-страницы нет). Тонкая обёртка над app-medications-panel (список медикаментов), плюс
 * шапка с «Изменить»/«Удалить» самой аптечки, переехавшими с уже неактуальной строки списка.
 */
@Component({
  selector: 'app-medkit-detail-page',
  standalone: true,
  imports: [FormsModule, BackLinkComponent, ModalComponent, LoadingSpinnerComponent, MedicationsPanelComponent],
  templateUrl: './medkit-detail-page.component.html',
})
export class MedkitDetailPageComponent implements OnInit, OnDestroy {
  readonly id = input.required<string>();

  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly pageAction = inject(PageActionService);

  medkit: Medkit | null = null;
  loading = true;
  error: string | null = null;

  showFormModal = false;
  form = { name: '' };

  // undefined — ещё ни разу не загружали.
  private loadedId: string | undefined = undefined;

  constructor() {
    // Реагирует на смену id, пока страница смонтирована (переход между аптечками сменой :id в
    // URL, без пересоздания компонента) — та же причина, что у medkits-panel.component.ts.
    effect(() => {
      const id = this.id();
      if (id === this.loadedId) return;
      void this.load();
    });
  }

  ngOnInit(): void {
    // Первичная загрузка — здесь, а не только в effect(): effect выполняется на следующем цикле
    // change detection и может не успеть отработать до первого рендера (тот же приём, что
    // medical-records-panel.component.ts).
    if (this.id() !== this.loadedId) void this.load();
    this.pageAction.setImmersive(true);
  }

  ngOnDestroy(): void {
    this.pageAction.clear();
  }

  async load(): Promise<void> {
    const id = this.id();
    this.loadedId = id;
    this.loading = true;
    try {
      this.medkit = await this.api.getMedkit(id);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить аптечку.';
    } finally {
      this.loading = false;
    }
  }

  goBack(): void {
    void this.router.navigate(['/health/medications']);
  }

  startEdit(): void {
    if (!this.medkit) return;
    this.form = { name: this.medkit.name };
    this.showFormModal = true;
  }

  closeFormModal(): void {
    this.showFormModal = false;
  }

  async saveEdit(): Promise<void> {
    if (!this.form.name.trim()) return;
    try {
      await this.api.updateMedkit(this.id(), { name: this.form.name.trim() });
      this.showFormModal = false;
      this.toast.success('Аптечка обновлена.');
      await this.load();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось сохранить аптечку.');
    }
  }

  async handleDelete(): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить аптечку?',
      message: 'Аптечка и все медикаменты в ней будут удалены безвозвратно.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteMedkit(this.id());
      this.toast.success('Аптечка удалена.');
      this.goBack();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось удалить аптечку.');
    }
  }

  /** Держит счётчик медикаментов в шапке актуальным после правок внутри app-medications-panel. */
  onCountChanged(count: number): void {
    if (this.medkit) this.medkit.medicationCount = count;
  }

  medicationCountLabel(count: number): string {
    return `${count} ${pluralizeRu(count, 'медикамент', 'медикамента', 'медикаментов')}`;
  }
}
