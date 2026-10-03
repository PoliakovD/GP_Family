import { describe, expect, it } from 'vitest';
import { ExtractionJobStatus, ExtractionStage } from '../../models/types';
import type { ExtractionStatusResponse } from '../../models/types';
import {
  currentFileNumber, enrichmentFollowupStep, friendlyExtractionError, nextPipelineSteps,
} from './extraction-pipeline';

const status = (over: Partial<ExtractionStatusResponse> = {}): ExtractionStatusResponse => ({
  status: ExtractionJobStatus.Running, stage: ExtractionStage.Ocr, indicatorCount: 0, error: null,
  totalFiles: 1, processedFiles: 0, createdAt: '', completedAt: null, queuePosition: 0,
  currentThought: null, waitingForAi: false, ...over,
});

describe('nextPipelineSteps', () => {
  it('first tick of a running job adds the stage as the active step', () => {
    const steps = nextPipelineSteps([], status(), null);
    expect(steps).toEqual([{ id: 'stage-2-0', label: 'Распознаём текст', state: 'active' }]);
  });

  it('does not mutate the input array', () => {
    const input = [{ id: 'a', label: 'x', state: 'active' as const }];
    nextPipelineSteps(input, status({ status: ExtractionJobStatus.Completed }), status());
    expect(input[0].state).toBe('active');
  });

  it('stage change marks the previous step done and adds the new one', () => {
    const first = nextPipelineSteps([], status({ stage: ExtractionStage.Ocr }), null);
    const next = nextPipelineSteps(first, status({ stage: ExtractionStage.Structuring }), status({ stage: ExtractionStage.Ocr }));
    expect(next.map((s) => s.state)).toEqual(['done', 'active']);
    expect(next[1].label).toBe('Считываем показатели');
  });

  it('shows «файл N из M» only on per-file stages', () => {
    const ocr = nextPipelineSteps([], status({ stage: ExtractionStage.Ocr, totalFiles: 3, processedFiles: 1 }), null);
    expect(ocr[0].label).toBe('Распознаём текст — файл 2 из 3');
    const linking = nextPipelineSteps([], status({ stage: ExtractionStage.Linking, totalFiles: 3, processedFiles: 3 }), null);
    expect(linking[0].label).toBe('Сверяем со справочником показателей');
  });

  it('a newly processed file gets its own done row', () => {
    const prev = status({ totalFiles: 2, processedFiles: 0 });
    const steps = nextPipelineSteps(nextPipelineSteps([], prev, null), status({ totalFiles: 2, processedFiles: 1 }), prev);
    expect(steps.some((s) => s.label === 'Файл 1 распознан' && s.state === 'done')).toBe(true);
  });

  it('queue position is described without internals and updates only when it changes', () => {
    const q = status({ status: ExtractionJobStatus.Pending, queuePosition: 2 });
    const steps = nextPipelineSteps([], q, null);
    expect(steps[0].label).toBe('В очереди на распознавание — перед вами 2 документа');
    expect(nextPipelineSteps(steps, q, q)).toHaveLength(1);
  });

  it('waiting for AI is a single active step', () => {
    const w = status({ status: ExtractionJobStatus.Pending, waitingForAi: true });
    const steps = nextPipelineSteps([], w, null);
    expect(steps[0].label).toContain('Ждём ИИ');
    expect(nextPipelineSteps(steps, w, w)).toHaveLength(1);
  });

  it('failure ends with an error step and a hint', () => {
    const steps = nextPipelineSteps([], status({ status: ExtractionJobStatus.Failed, error: 'Файл повреждён' }), null);
    expect(steps.at(-1)).toMatchObject({ state: 'error' });
    expect(steps.at(-1)!.label).toBe('Файл повреждён. Можно нажать «Распознать» ещё раз или внести показатели вручную.');
  });

  it('completion ends with «Готово»', () => {
    const steps = nextPipelineSteps([{ id: 'x', label: 'y', state: 'active' }], status({ status: ExtractionJobStatus.Completed }), status());
    expect(steps.map((s) => [s.label, s.state])).toEqual([['y', 'done'], ['Готово', 'done']]);
  });
});

describe('friendlyExtractionError', () => {
  it('keeps human Russian text and hides technical/English/HTML', () => {
    expect(friendlyExtractionError('Нет текста на фото')).toBe('Нет текста на фото.');
    expect(friendlyExtractionError('NullReferenceException at ...')).toBe('Не удалось распознать документ.');
    expect(friendlyExtractionError('<html>Ошибка</html>')).toBe('Не удалось распознать документ.');
    expect(friendlyExtractionError(null)).toBe('Не удалось распознать документ.');
  });
});

describe('helpers', () => {
  it('currentFileNumber is capped by totalFiles', () => {
    expect(currentFileNumber(status({ processedFiles: 3, totalFiles: 3 }))).toBe(3);
    expect(currentFileNumber(status({ processedFiles: 0, totalFiles: 3 }))).toBe(1);
  });

  it('enrichmentFollowupStep only when something is pending', () => {
    expect(enrichmentFollowupStep(0)).toBeNull();
    expect(enrichmentFollowupStep(2)?.label).toContain('Для 2 показателей');
  });
});
