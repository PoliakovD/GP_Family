// Живой прогресс распознавания записи — чистые функции без состояния компонента (вынесено из
// MedicalRecordsPanelComponent, чтобы логику шагов можно было проверить тестами).

import { ExtractionJobStatus, ExtractionStage } from '../../models/types';
import type { ExtractionStatusResponse } from '../../models/types';
import type { PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';
import { pluralizeRu } from '../../shared/util/pluralize';

/** Терминальные статусы задачи распознавания — опрос останавливается. */
export const EXTRACTION_TERMINAL_STATUSES: number[] = [
  ExtractionJobStatus.Completed, ExtractionJobStatus.Failed, ExtractionJobStatus.Skipped,
];

// Информативнее прежних коротких подписей ("Распознаём"/"Извлекаем данные") — пользователь просил
// видеть, что именно сейчас происходит на каждом шаге, а не общие слова.
export const STAGE_LABEL: Partial<Record<number, string>> = {
  [ExtractionStage.Queued]: 'В очереди',
  [ExtractionStage.Decoding]: 'Открываем файл',
  [ExtractionStage.Ocr]: 'Распознаём текст',
  [ExtractionStage.Structuring]: 'Считываем показатели',
  [ExtractionStage.Linking]: 'Сверяем со справочником показателей',
  [ExtractionStage.Summarizing]: 'Готовим резюме анализа',
};

/** «Файл N из totalFiles» в процессе распознавания — processedFiles уже завершены, текущий —
 * следующий по счёту (капнуто totalFiles на случай отставания статуса от факта). */
export function currentFileNumber(status: ExtractionStatusResponse): number {
  return Math.min(status.processedFiles + 1, status.totalFiles);
}

/** Текст бэкенда про сбой распознавания — только если он по-русски и человеческий; иначе общий. */
export function friendlyExtractionError(raw: string | null | undefined): string {
  if (raw && /[а-яё]/i.test(raw) && !raw.includes('<') && raw.length <= 300) return raw.endsWith('.') ? raw : raw + '.';
  return 'Не удалось распознать документ.';
}

/**
 * Следующее состояние живого списка шагов (UX-редизайн) — растущий список «уже сделано» +
 * текущий пульсирующий шаг, не статичная строка. Только выполненные + активный: будущие шаги не
 * показываем, конвейер может их пропустить (текстовый путь не заходит в OCR). Входной массив не
 * мутируется.
 */
export function nextPipelineSteps(
  current: readonly PipelineStep[],
  status: ExtractionStatusResponse,
  prev: ExtractionStatusResponse | null,
): PipelineStep[] {
  const steps = [...current];
  const markLastDone = () => {
    const last = steps[steps.length - 1];
    if (last && last.state === 'active') steps[steps.length - 1] = { ...last, state: 'done' };
  };

  if (status.status === ExtractionJobStatus.Failed || status.status === ExtractionJobStatus.Skipped) {
    markLastDone();
    steps.push({
      id: `outcome-${steps.length}`,
      label: `${friendlyExtractionError(status.error)} Можно нажать «Распознать» ещё раз или внести показатели вручную.`,
      state: 'error',
    });
  } else if (status.status === ExtractionJobStatus.Completed) {
    markLastDone();
    steps.push({ id: `outcome-${steps.length}`, label: 'Готово', state: 'done' });
  } else if (status.waitingForAi) {
    // ИИ (LM Studio) недоступен — задача не потеряна, стоит в очереди и стартует сама, как только
    // сервер вернётся (LmStudioRecoverySweepJob). Явно говорим об этом, чтобы «в процессе» не
    // выглядело как зависание и пользователь не жал «Распознать» повторно.
    if (!prev || !prev.waitingForAi || steps.length === 0) {
      markLastDone();
      steps.push({
        id: `waiting-ai-${steps.length}`,
        label: 'Ждём ИИ — документ сохранён и будет распознан автоматически, как только он станет доступен. Страницу можно закрыть.',
        state: 'active',
      });
    }
  } else if (status.queuePosition > 0) {
    // Общая очередь к единственной локальной модели: под нагрузкой Status уже мог стать Running и
    // Stage "Ocr"/"Decoding", но сама модель занята задачей ДРУГОГО конвейера (QueuePosition на
    // бэкенде). Без этой проверки пользователь видел бы «Распознаём текст», хотя задача ждёт
    // очереди. Проверяется ДО ветки по стадиям ниже.
    if (!prev || prev.queuePosition !== status.queuePosition || steps.length === 0) {
      markLastDone();
      const label = `В очереди на распознавание — перед вами ${status.queuePosition} ` +
        `${pluralizeRu(status.queuePosition, 'документ', 'документа', 'документов')}`;
      steps.push({ id: `global-queue-${status.queuePosition}`, label, state: 'active' });
    }
  } else if (status.status === ExtractionJobStatus.Pending) {
    // Никого нет впереди (queuePosition===0) — ждём, пока воркер реально возьмёт задачу.
    if (!prev || steps.length === 0 || prev.queuePosition > 0 || prev.waitingForAi) {
      markLastDone();
      steps.push({ id: 'queue-next', label: 'В очереди — следующая на распознавание', state: 'active' });
    }
  } else {
    // Новый обработанный файл — отдельная строка с галочкой, до перехода к следующей стадии.
    if (prev && status.processedFiles > prev.processedFiles) {
      markLastDone();
      steps.push({ id: `file-${status.processedFiles}`, label: `Файл ${status.processedFiles} распознан`, state: 'done' });
    }
    if (!prev || prev.stage !== status.stage || steps.length === 0) {
      markLastDone();
      const base = STAGE_LABEL[status.stage] ?? 'Обрабатываем…';
      // «файл N из M» — только на пофайловых стадиях (Decoding/Ocr): Structuring/Linking/Summarizing
      // идут один раз на всю запись, и «файл 5 из 5» там читалось как «всё ещё файл 5» (казалось,
      // что процесс завис при переходе к этим стадиям).
      const isPerFileStage = status.stage === ExtractionStage.Decoding || status.stage === ExtractionStage.Ocr;
      const label = isPerFileStage && status.totalFiles > 1
        ? `${base} — файл ${currentFileNumber(status)} из ${status.totalFiles}`
        : base;
      steps.push({ id: `stage-${status.stage}-${steps.length}`, label, state: 'active' });
    }
  }
  return steps;
}

/**
 * §6 плана «живой конвейер» — после «Готово» предупреждаем, что часть работы продолжится в фоне,
 * если среди сохранённых показателей есть промахнувшиеся по справочнику. null — добавлять нечего.
 */
export function enrichmentFollowupStep(pendingCount: number): PipelineStep | null {
  if (pendingCount === 0) return null;
  return {
    id: 'enrichment-followup',
    label: `Для ${pendingCount} ${pluralizeRu(pendingCount, 'показателя', 'показателей', 'показателей')} ещё уточняем норму — можно закрыть страницу, это продолжится само`,
    state: 'done',
  };
}
