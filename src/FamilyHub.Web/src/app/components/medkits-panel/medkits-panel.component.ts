import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import type { Medkit } from '../../models/types';
import { MedicationsPanelComponent } from '../medications-panel/medications-panel.component';
import { ToastService } from '../../shared/toast/toast.service';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { ModalComponent } from '../../shared/modal/modal.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { pluralizeRu } from '../../shared/util/pluralize';

@Component({
    selector: 'app-medkits-panel',
    imports: [FormsModule, MedicationsPanelComponent, ModalComponent, LoadingSpinnerComponent],
    templateUrl: './medkits-panel.component.html',
    styleUrl: './medkits-panel.component.scss'
})
export class MedkitsPanelComponent implements OnInit {
  readonly familyId = input.required<string>();

  /** Редизайн v2.2 — на вкладке «Аптечка» (medications-tab) строка аптечки уводит на отдельную
   * страницу (/health/medications/:id), как уже сделано для «Анализов» — тот же экран несёт и
   * «Изменить»/«Удалить» самой аптечки, поэтому в этом режиме они скрыты из строки списка (см.
   * шаблон). false по умолчанию сохраняет прежний аккордеон без изменений — этой же панелью
   * пользуется family-details.component.html («Семья» → «Аптечки»), где отдельной
   * деталь-страницы нет и заводить её не просили. */
  readonly linkMode = input(false);

  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  items: Medkit[] = [];
  form = { name: '' };
  editingId: string | null = null;
  expandedId: string | null = null;
  error: string | null = null;
  showFormModal = false;
  loading = true;

  // Аптечки, которые открывали хотя бы раз — их содержимое остаётся смонтированным (не
  // выгружается при сворачивании), иначе плавную CSS-анимацию сворачивания не на чем строить.
  // Загрузка при этом всё равно ленивая: до первого клика ничего не монтируется и не запрашивается.
  private readonly everOpenedIds = new Set<string>();

  // undefined — ещё ни разу не загружали.
  private loadedFamilyId: string | undefined = undefined;

  constructor() {
    // Реагирует на смену семьи, пока панель открыта. Первичная загрузка при монтировании —
    // в ngOnInit (effect() выполняется только на следующем цикле change detection и может не
    // успеть отработать).
    effect(() => {
      const id = this.familyId();
      if (id === this.loadedFamilyId) return;
      this.resetForm();
      this.expandedId = null;
      this.everOpenedIds.clear();
      void this.refresh();
    });
  }

  ngOnInit(): void {
    if (this.familyId() !== this.loadedFamilyId) {
      void this.refresh();
    }
  }

  async refresh(): Promise<void> {
    const id = this.familyId();
    this.loadedFamilyId = id;
    this.loading = true;
    try {
      this.items = await this.api.getMedkits(id);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить аптечки.';
    } finally {
      this.loading = false;
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.name.trim()) return;
    const payload = { name: this.form.name.trim() };
    const isEdit = !!this.editingId;
    try {
      if (this.editingId) {
        await this.api.updateMedkit(this.editingId, payload);
      } else {
        await this.api.createMedkit(this.familyId(), payload);
      }
      this.resetForm();
      this.showFormModal = false;
      this.toast.success(isEdit ? 'Аптечка обновлена.' : 'Аптечка создана.');
      await this.refresh();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось сохранить аптечку.');
    }
  }

  openCreateModal(): void {
    this.resetForm();
    this.showFormModal = true;
  }

  startEdit(item: Medkit): void {
    this.editingId = item.id;
    this.form = { name: item.name };
    this.showFormModal = true;
  }

  closeFormModal(): void {
    this.showFormModal = false;
    this.resetForm();
  }

  async handleDelete(id: string): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить аптечку?',
      message: 'Аптечка и все медикаменты в ней будут удалены безвозвратно.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteMedkit(id);
      if (this.expandedId === id) this.expandedId = null;
      this.toast.success('Аптечка удалена.');
      await this.refresh();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось удалить аптечку.');
    }
  }

  /** Клик по строке аптечки — в linkMode уводит на отдельную страницу (как «Открыть» у записи
   * анализа, только тут кликабельна вся строка сразу, без промежуточной кнопки), иначе — старый
   * аккордеон. */
  onHeaderClick(item: Medkit): void {
    if (this.linkMode()) {
      void this.router.navigate(['/health/medications', item.id]);
      return;
    }
    this.toggleExpanded(item.id);
  }

  toggleExpanded(id: string): void {
    if (this.expandedId === id) {
      this.expandedId = null;
      return;
    }
    this.expandedId = id;
    this.everOpenedIds.add(id);
  }

  wasEverOpened(id: string): boolean {
    return this.everOpenedIds.has(id);
  }

  /** Держит счётчик в свёрнутой карточке в актуальном состоянии после правок внутри вложенной панели. */
  onMedicationCountChanged(item: Medkit, count: number): void {
    item.medicationCount = count;
  }

  /** Склонение "N медикамент/медикамента/медикаментов" для счётчика в свёрнутой карточке. */
  medicationCountLabel(count: number): string {
    return `${count} ${pluralizeRu(count, 'медикамент', 'медикамента', 'медикаментов')}`;
  }

  resetForm(): void {
    this.form = { name: '' };
    this.editingId = null;
  }
}
