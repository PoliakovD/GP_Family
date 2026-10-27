// Фикстуры админ-панели (/admin/...) для visual/shots.mjs — тот же формат, что SCREENS/ROUTES
// в fixtures.mjs (подключаются оттуда). Типы ответов — src/FamilyHub.Web/src/app/services/admin-api.service.ts.
// В данных намеренно смесь состояний (ошибки/предупреждения/норма), чтобы на снимках были видны
// все цвета статусов. «Сегодня» — 14.10.2026 09:40 МСК (shots.mjs).

/** Экраны админки. Без `only` — desktop и mobile. */
export const ADMIN_SCREENS = [
  { name: 'admin-login', path: '/admin/login', anon: true },
  { name: 'admin-attention', path: '/admin/attention' },
  { name: 'admin-review', path: '/admin/review' },
  // Карточка справа открыта всегда (первая строка выбрана сама) — здесь кликаем строку стадии
  // «результат», чтобы снять карточку с черновиком, источниками и сравнением со справочником.
  { name: 'admin-review-detail', path: '/admin/review', only: 'desktop', prepare: async (page) => {
    await page.locator('.inbox-row').filter({ hasText: 'Ферритин' }).first().click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(300);
  } },
  { name: 'admin-monitoring-overview', path: '/admin/monitoring/overview' },
  { name: 'admin-monitoring-storage', path: '/admin/monitoring/storage', only: 'desktop' },
  { name: 'admin-monitoring-services', path: '/admin/monitoring/services', only: 'desktop' },
  { name: 'admin-security-keys', path: '/admin/security/keys', only: 'desktop' },
  { name: 'admin-security-credentials', path: '/admin/security/credentials', only: 'desktop' },
  { name: 'admin-security-stats', path: '/admin/security/stats', only: 'desktop' },
  { name: 'admin-settings-ai-model', path: '/admin/settings/ai-model', only: 'desktop' },
  { name: 'admin-settings-pipeline-steps', path: '/admin/settings/pipeline-steps', only: 'desktop' },
  { name: 'admin-settings-prompts', path: '/admin/settings/prompts', only: 'desktop' },
  { name: 'admin-settings-web-search', path: '/admin/settings/web-search', only: 'desktop' },
  { name: 'admin-settings-env', path: '/admin/settings/env', only: 'desktop' },
  { name: 'admin-operations-jobs', path: '/admin/operations/jobs' },
  { name: 'admin-operations-jobs-detail', path: '/admin/operations/jobs?type=lab-analyte&job=j-ferritin', only: 'desktop' },
  { name: 'admin-operations-warmup', path: '/admin/operations/warmup', only: 'desktop' },
  { name: 'admin-operations-search-cache', path: '/admin/operations/search-cache', only: 'desktop' },
  { name: 'admin-operations-rebuilds', path: '/admin/operations/rebuilds', only: 'desktop' },
  { name: 'admin-operations-search-calls', path: '/admin/operations/search-calls', only: 'desktop' },
  { name: 'admin-operations-changes', path: '/admin/operations/changes', only: 'desktop' },
  { name: 'admin-catalog-analytes', path: '/admin/catalog/analytes' },
  { name: 'admin-catalog-analytes-detail', path: '/admin/catalog/analytes?open=kb-ferritin', only: 'desktop' },
  { name: 'admin-catalog-medications', path: '/admin/catalog/medications', only: 'desktop' },
  { name: 'admin-catalog-specimens', path: '/admin/catalog/specimens', only: 'desktop' },
];

// ------------------------------------------------------------------ общие куски

const T = (day, time = '09:00') => `2026-10-${String(day).padStart(2, '0')}T${time}:00+03:00`;

const ANALYTE_PAYLOAD = JSON.stringify({
  schemaVersion: 4,
  loincCode: '2276-4',
  defaultUnit: 'мкг/л',
  plainExplanation: 'Белок, в котором организм хранит железо. Показывает запасы железа.',
  whyMeasured: 'Чтобы понять, есть ли дефицит или избыток железа, и отличить железодефицитную анемию от других.',
  highMeans: 'Бывает при воспалении, болезнях печени и избытке железа.',
  lowMeans: 'Почти всегда означает, что запасы железа истощены.',
  calculationInstructions: null,
  relatedNames: ['гемоглобин', 'сывороточное железо', 'трансферрин'],
  refRanges: [
    { ageFrom: 18, ageTo: null, sex: 'male', low: 30, high: 400, unit: 'мкг/л', normKind: 'fixed', population: 'general', populationDetail: null, sourceDomain: 'helix.ru', sourceRank: 1 },
    { ageFrom: 18, ageTo: null, sex: 'female', low: 13, high: 150, unit: 'мкг/л', normKind: 'fixed', population: 'general', populationDetail: null, sourceDomain: 'invitro.ru', sourceRank: 2 },
  ],
}, null, 2);

const MEDICATION_PAYLOAD = JSON.stringify({
  schemaVersion: 3,
  internationalName: 'Ибупрофен',
  tradeNames: ['Нурофен', 'МИГ 400', 'Ибуклин'],
  form: 'таблетки, покрытые оболочкой, 200 мг и 400 мг',
  purpose: 'Нестероидный противовоспалительный препарат: снимает боль, жар и воспаление.',
  simplePurpose: 'От боли и температуры.',
  usage: 'Взрослым по 200–400 мг до 3 раз в сутки после еды, не дольше 5 дней без врача.',
  storage: 'При температуре не выше 25 °C, в недоступном для детей месте.',
  driving: 'Не влияет.',
  specialNotes: 'Нельзя при язве желудка, в третьем триместре беременности и при аллергии на НПВП.',
}, null, 2);

const SPECIMENS = [
  { id: 'sp-blood', displayName: 'Кровь', normalizedName: 'кровь', source: 'seed', createdAt: '2026-03-01T10:00:00Z', aliases: ['цельная кровь', 'венозная кровь'] },
  { id: 'sp-serum', displayName: 'Сыворотка крови', normalizedName: 'сыворотка крови', source: 'seed', createdAt: '2026-03-01T10:00:00Z', aliases: ['сыворотка'] },
  { id: 'sp-plasma', displayName: 'Плазма крови', normalizedName: 'плазма крови', source: 'ai', createdAt: '2026-05-14T12:30:00Z', aliases: [] },
  { id: 'sp-urine', displayName: 'Моча', normalizedName: 'моча', source: 'seed', createdAt: '2026-03-01T10:00:00Z', aliases: ['моча разовая', 'утренняя моча'] },
  { id: 'sp-stool', displayName: 'Кал', normalizedName: 'кал', source: 'ai', createdAt: '2026-06-02T08:15:00Z', aliases: ['фекалии'] },
  { id: 'sp-capillary', displayName: 'Капиллярная кровь', normalizedName: 'капиллярная кровь', source: 'ai', createdAt: '2026-09-21T16:40:00Z', aliases: [] },
];

const SPECIMEN_GROUPS = [
  { id: 'sp-blood', displayName: 'Кровь', searchGroupKey: 'blood', cacheRows: 214 },
  { id: 'sp-serum', displayName: 'Сыворотка крови', searchGroupKey: 'blood', cacheRows: 96 },
  { id: 'sp-plasma', displayName: 'Плазма крови', searchGroupKey: 'blood', cacheRows: 31 },
  { id: 'sp-urine', displayName: 'Моча', searchGroupKey: 'urine', cacheRows: 42 },
  { id: 'sp-stool', displayName: 'Кал', searchGroupKey: null, cacheRows: 7 },
  { id: 'sp-capillary', displayName: 'Капиллярная кровь', searchGroupKey: null, cacheRows: 0 },
];

const TRUSTED_DOMAINS_MED = [
  { id: 'td-1', domain: 'grls.rosminzdrav.ru', rank: 1, isEnabled: true },
  { id: 'td-2', domain: 'vidal.ru', rank: 2, isEnabled: true },
  { id: 'td-3', domain: 'rlsnet.ru', rank: 3, isEnabled: true },
  { id: 'td-4', domain: 'apteka.ru', rank: 4, isEnabled: false },
];
const TRUSTED_DOMAINS_LAB = [
  { id: 'td-11', domain: 'invitro.ru', rank: 1, isEnabled: true },
  { id: 'td-12', domain: 'helix.ru', rank: 2, isEnabled: true },
  { id: 'td-13', domain: 'cmd-online.ru', rank: 3, isEnabled: true },
  { id: 'td-14', domain: 'gemotest.ru', rank: 4, isEnabled: true },
  { id: 'td-15', domain: 'labtestsonline.org', rank: 5, isEnabled: false },
];

const snippet = (title, url, text, extra = {}) => {
  const domain = new URL(url).host;
  return { title, url, text, domain, isTrustedByDomain: true, override: null, enabled: true,
    origin: 'Auto', kind: null, note: null, pinned: false, ...extra };
};

const CACHE_SNIPPETS_FERRITIN = [
  snippet('Ферритин — Инвитро', 'https://invitro.ru/analizes/for-doctors/ferritin/',
    'Ферритин — основной белок, депонирующий железо. Референсные значения для мужчин 30–400 мкг/л, для женщин 13–150 мкг/л.'),
  snippet('Ферритин в крови — Хеликс', 'https://helix.ru/kb/item/06-020',
    'Снижение ферритина — самый ранний и чувствительный признак дефицита железа.', { pinned: true }),
  snippet('Ферритин: норма и отклонения', 'https://zdorovie-blog.example/ferritin',
    'Ферритин повышается при любом воспалении, поэтому его надо смотреть вместе с СРБ.',
    { isTrustedByDomain: false, enabled: false }),
  snippet('Заметка эксперта', 'expert://admin/ferritin',
    'У доноров крови ферритин часто ниже 30 мкг/л без анемии.', { origin: 'Manual', kind: 'expert-knowledge', note: 'Со слов гематолога', isTrustedByDomain: false }),
];

const CACHE_DETAIL_FERRITIN = {
  id: 'sc-ferritin', normalizedName: 'ферритин', displayName: 'Ферритин', specimen: 'Сыворотка крови', provider: 'yandex',
  lastUpdatedAt: T(12, '14:20'), canBeUpdatedAfter: '2027-01-12T14:20:00+03:00', snippets: CACHE_SNIPPETS_FERRITIN, units: 'мкг/л; нг/мл',
};

// ------------------------------------------------------------------ Требует внимания

const ATTENTION = {
  reasons: [
    { reason: 'NoTrustedSnippets', label: 'Нет сниппетов с доверенных доменов', count: 9, distinctCount: 7, byType: { 'lab-analyte': 6, medication: 3 } },
    { reason: 'LmStudioUnavailable', label: 'LM Studio недоступна', count: 4, distinctCount: 4, byType: { extraction: 2, 'lab-analyte': 2 } },
    { reason: 'Plausibility', label: 'Не прошёл проверку правдоподобия', count: 3, distinctCount: 3, byType: { 'lab-analyte': 3 } },
    { reason: 'ProviderFailed', label: 'Ошибка поискового провайдера', count: 2, distinctCount: 2, byType: { medication: 1, 'visit-medication': 1 } },
    { reason: 'Unclassified', label: 'Без причины (старые задачи)', count: 5, distinctCount: 5, byType: { medication: 5 } },
  ],
  droppedDomains: [
    { domain: 'zdorovie-blog.example', topic: 'LabAnalyte', jobCount: 4, sampleUrl: 'https://zdorovie-blog.example/ferritin' },
    { domain: 'eapteka.ru', topic: 'Medication', jobCount: 3, sampleUrl: 'https://www.eapteka.ru/goods/id212345/' },
    { domain: 'medportal.ru', topic: 'LabAnalyte', jobCount: 2, sampleUrl: 'https://medportal.ru/enc/analysis/blood/ttg/' },
  ],
  webSearchPaused: { isPaused: false, pausedAt: null, note: null, deferredTotal: 0, byType: {} },
  reviewQueue: { searches: 4, results: 3, total: 7 },
};

// ------------------------------------------------------------------ Одобрение

const inboxItem = (id, kind, stage, name, specimen, confidence, threshold, origin, createdAt, extra = {}) => ({
  id, kind, stage, name, specimen, queryText: stage === 'search' ? name : null,
  confidence, confidenceReason: confidence === null ? null : (confidence < threshold ? 'Мало совпадений с доверенными источниками' : 'Название однозначно'),
  threshold, belowThreshold: confidence === null || confidence < threshold, origin, createdAt,
  hasTwins: false, hasNote: false, ...extra,
});

const REVIEW_ROWS = [
  inboxItem('rv-ttg', 'lab-analyte', 'search', 'ТТГ', 'Сыворотка крови', 0.42, 0.7, 'extraction', T(14, '08:55'),
    { queryText: 'тиреотропный гормон ТТГ норма сыворотка' }),
  inboxItem('rv-ferritin', 'lab-analyte', 'result', 'Ферритин', 'Сыворотка крови', 0.61, 0.75, 'extraction', T(14, '08:31'), { hasNote: true }),
  inboxItem('rv-mig', 'medication', 'search', 'МИГ 400', null, null, 0.6, 'medkit', T(13, '21:10')),
  inboxItem('rv-hb', 'lab-analyte', 'search', 'Гемоглобин', 'Капиллярная кровь', 0.83, 0.7, 'manual', T(13, '18:02'), { hasTwins: true }),
  inboxItem('rv-ibu', 'medication', 'result', 'Ибупрофен', null, 0.88, 0.8, 'medkit', T(13, '12:47')),
  inboxItem('rv-amox', 'visit-medication', 'result', 'Амоксиклав', null, 0.55, 0.8, 'visit', T(12, '16:20')),
  inboxItem('rv-vitd', 'lab-analyte', 'search', '25-OH витамин D', 'Сыворотка крови', 0.74, 0.7, 'maintenance', T(11, '03:00')),
];

const reviewSource = (index, s, extra = {}) => ({
  index, title: s.title, url: s.url, text: s.text, domain: s.domain, trustedByDomain: s.isTrustedByDomain,
  enabled: s.enabled, override: s.override, origin: s.origin, kind: s.kind, note: s.note, pinned: s.pinned,
  usedInDraft: s.enabled && index < 2, untrustedWarning: s.origin === 'Manual' && !s.isTrustedByDomain, ...extra,
});

const reviewCache = (topic, extra = {}) => ({
  cacheId: 'sc-ferritin', topic, provider: 'yandex', lastUpdatedAt: T(12, '14:20'), canBeUpdatedAfter: '2027-01-12T14:20:00+03:00',
  fresh: true, snippetCount: 4, manualCount: 1, searchGroupKey: 'blood', queryLabel: 'ферритин сыворотка норма', ...extra,
});

const CURRENT_FERRITIN = {
  id: 'kb-ferritin', displayName: 'Ферритин', aliases: ['ферритин сыворотки'], lockedFields: [], updatedAt: '2026-06-03T11:00:00Z',
  verificationStatus: 0, verifiedAt: null, verificationStale: false,
  payloadJson: JSON.stringify({ ...JSON.parse(ANALYTE_PAYLOAD), plainExplanation: 'Показывает запасы железа в организме.',
    refRanges: [JSON.parse(ANALYTE_PAYLOAD).refRanges[0]] }, null, 2),
};

function reviewItem(id, kind) {
  const row = REVIEW_ROWS.find((r) => r.id === id) ?? REVIEW_ROWS[0];
  const isLab = row.kind === 'lab-analyte';
  const topic = isLab ? 'lab-analyte' : 'medication';
  const sources = (isLab ? CACHE_SNIPPETS_FERRITIN : [
    snippet('Ибупрофен — Видаль', 'https://www.vidal.ru/drugs/molecule/39', 'Ибупрофен оказывает анальгезирующее, жаропонижающее и противовоспалительное действие.'),
    snippet('Нурофен — ГРЛС', 'https://grls.rosminzdrav.ru/Grls_View_v2.aspx?routingGuid=1', 'Таблетки, покрытые оболочкой, 200 мг. Применять внутрь после еды.'),
    snippet('Ибупрофен: инструкция', 'https://www.eapteka.ru/goods/id212345/', 'Не принимать более 1200 мг в сутки без назначения врача.', { isTrustedByDomain: false, enabled: false }),
  ]).map((s, i) => reviewSource(i, s));
  const result = row.stage === 'result';
  return {
    id: row.id, kind: kind ?? row.kind, stage: row.stage, name: row.name, specimen: row.specimen, origin: row.origin,
    provider: 'yandex', createdAt: row.createdAt,
    queryText: row.queryText ?? (isLab ? `${row.name.toLowerCase()} ${row.specimen?.toLowerCase() ?? ''} норма` : `${row.name} инструкция`),
    queryConfidence: result ? 0.9 : row.confidence, queryConfidenceReason: result ? 'Название однозначно' : row.confidenceReason,
    queryThreshold: 0.7, queryBelowThreshold: result ? false : row.belowThreshold,
    resultConfidence: result ? row.confidence : null, resultConfidenceReason: result ? row.confidenceReason : null,
    resultThreshold: row.threshold, resultBelowThreshold: result ? row.belowThreshold : false,
    draft: result ? {
      normalizedName: row.name.toLowerCase(), displayName: row.name,
      payloadJson: isLab ? ANALYTE_PAYLOAD : MEDICATION_PAYLOAD,
      aliases: isLab ? ['ферритин сыворотки', 'ферритин крови'] : ['ибупрофен 200 мг'], source: 'ai',
    } : null,
    current: result && isLab ? CURRENT_FERRITIN : null,
    fieldSourceInfo: result ? {
      available: true,
      fieldSources: isLab
        ? { plainExplanation: ['https://helix.ru/kb/item/06-020'], refRanges: ['https://invitro.ru/analizes/for-doctors/ferritin/', 'https://helix.ru/kb/item/06-020'], lowMeans: ['https://helix.ru/kb/item/06-020'] }
        : { purpose: ['https://www.vidal.ru/drugs/molecule/39'], usage: ['https://grls.rosminzdrav.ru/Grls_View_v2.aspx?routingGuid=1'] },
      fieldsWithoutSource: isLab ? ['highMeans'] : ['driving'],
    } : null,
    cache: reviewCache(topic, isLab ? {} : { cacheId: 'sc-ibu', searchGroupKey: null, manualCount: 0, snippetCount: 3, queryLabel: 'ибупрофен инструкция' }),
    sources,
    twins: row.hasTwins ? [{ cacheId: 'sc-hb-blood', specimen: 'Кровь', searchGroupKey: 'blood', provider: 'yandex', lastUpdatedAt: T(2, '10:00'), snippetCount: 5 }] : [],
    note: row.hasNote ? 'Сравнить нормы с бланком Инвитро — у женщин нижняя граница расходится.' : null,
    kbMatch: row.id === 'rv-ttg' ? { kbId: 'kb-ttg', displayName: 'Тиреотропный гормон (ТТГ)', reason: 'unit-gap', score: 0.93, units: 'мМЕ/л' } : null,
  };
}

// ------------------------------------------------------------------ Задачи конвейера

const pjob = (id, type, displayName, status, extra = {}) => ({
  id, type, displayName, status, attempts: status === 'Failed' ? 3 : 1, error: null,
  createdAt: T(14, '08:00'), startedAt: status === 'Pending' ? null : T(14, '08:01'),
  completedAt: ['Completed', 'Failed', 'Skipped'].includes(status) ? T(14, '08:03') : null, failureReason: null, ...extra,
});

const JOBS = {
  'lab-analyte': [
    pjob('j-ferritin', 'lab-analyte', 'Ферритин · Сыворотка крови', 'Failed', { failureReason: 'NoTrustedSnippets', error: 'Ни один сниппет не пришёл с доверенного домена (3 отброшено).' }),
    pjob('j-ttg', 'lab-analyte', 'ТТГ · Сыворотка крови', 'AwaitingSearchApproval', { createdAt: T(14, '08:55'), startedAt: null }),
    pjob('j-hb', 'lab-analyte', 'Гемоглобин · Кровь', 'Completed', { createdAt: T(13, '18:00'), startedAt: T(13, '18:01'), completedAt: T(13, '18:02') }),
    pjob('j-crp', 'lab-analyte', 'С-реактивный белок · Сыворотка крови', 'Running', { startedAt: T(14, '09:38') }),
    pjob('j-alt', 'lab-analyte', 'АЛТ · Сыворотка крови', 'Failed', { failureReason: 'LmStudioUnavailable', error: 'LM Studio не ответила за 120 с (http://lmstudio:1234).' }),
    pjob('j-vitd', 'lab-analyte', '25-OH витамин D · Сыворотка крови', 'Deferred', { startedAt: null }),
    pjob('j-glu', 'lab-analyte', 'Глюкоза · Плазма крови', 'Skipped', { error: 'Уже есть в справочнике.' }),
    pjob('j-esr', 'lab-analyte', 'СОЭ · Кровь', 'Pending', { createdAt: T(14, '09:39') }),
  ],
  medication: [
    pjob('j-ibu', 'medication', 'Ибупрофен', 'Completed'),
    pjob('j-mig', 'medication', 'МИГ 400', 'AwaitingSearchApproval', { startedAt: null }),
    pjob('j-omez', 'medication', 'Омез', 'Failed', { failureReason: 'ProviderFailed', error: 'Яндекс.Поиск вернул 503 Service Unavailable.' }),
    pjob('j-d3', 'medication', 'Аквадетрим', 'Completed'),
    pjob('j-sumatr', 'medication', 'Суматриптан', 'Failed', { failureReason: null, error: 'NullReferenceException в MedicationEnrichmentJob' }),
  ],
  'visit-medication': [
    pjob('j-amox', 'visit-medication', 'Амоксиклав', 'AwaitingResultReview'),
    pjob('j-nazivin', 'visit-medication', 'Називин', 'Completed'),
    pjob('j-aqua', 'visit-medication', 'Аквамарис', 'Failed', { failureReason: 'Legitimacy', error: 'Название не похоже на лекарственный препарат.' }),
  ],
  extraction: [
    pjob('j-ex1', 'extraction', 'Анализ крови · 03.10.2026', 'Completed'),
    pjob('j-ex2', 'extraction', 'Биохимия · 12.08.2026', 'Failed', { failureReason: 'LmStudioUnavailable', error: 'LM Studio недоступна.' }),
    pjob('j-ex3', 'extraction', 'Заключение терапевта · 14.10.2026', 'Running', { startedAt: T(14, '09:35') }),
  ],
};

function jobDetail(id, type) {
  const job = Object.values(JOBS).flat().find((j) => j.id === id) ?? JOBS['lab-analyte'][0];
  const isLab = job.type === 'lab-analyte';
  return {
    ...job, type: type ?? job.type,
    normalizedName: job.displayName.split(' · ')[0].toLowerCase(),
    specimenKbId: isLab ? 'sp-serum' : null, specimenDisplayName: isLab ? 'Сыворотка крови' : null,
    origin: isLab ? 'extraction' : null, force: false, provider: 'yandex', externalSearchAt: T(14, '08:02'),
    isTransientFailure: job.failureReason === 'LmStudioUnavailable' || job.failureReason === 'ProviderFailed',
    kbId: job.status === 'Completed' ? 'kb-hb' : null,
    searchCache: job.type === 'extraction' ? null : (isLab ? CACHE_DETAIL_FERRITIN : { ...CACHE_DETAIL_FERRITIN, id: 'sc-ibu', normalizedName: 'ибупрофен', displayName: 'Ибупрофен', specimen: null, units: null }),
    trustedDomains: isLab ? TRUSTED_DOMAINS_LAB : TRUSTED_DOMAINS_MED,
  };
}

// ------------------------------------------------------------------ Справочник

const ANALYTES = [
  { id: 'kb-hb', displayName: 'Гемоглобин', specimenKbId: 'sp-blood', specimenDisplayName: 'Кровь', preview: 'Белок эритроцитов, переносит кислород.', verificationStatus: 1, verificationStale: false, updatedAt: '2026-09-28T10:00:00Z' },
  { id: 'kb-ferritin', displayName: 'Ферритин', specimenKbId: 'sp-serum', specimenDisplayName: 'Сыворотка крови', preview: 'Показывает запасы железа в организме.', verificationStatus: 0, verificationStale: false, updatedAt: '2026-06-03T11:00:00Z' },
  { id: 'kb-ttg', displayName: 'Тиреотропный гормон (ТТГ)', specimenKbId: 'sp-serum', specimenDisplayName: 'Сыворотка крови', preview: 'Гормон гипофиза, управляет щитовидной железой.', verificationStatus: 2, verificationStale: true, updatedAt: '2026-10-10T15:30:00Z' },
  { id: 'kb-glu', displayName: 'Глюкоза', specimenKbId: 'sp-plasma', specimenDisplayName: 'Плазма крови', preview: 'Уровень сахара в крови.', verificationStatus: 3, verificationStale: false, updatedAt: '2026-08-12T09:00:00Z' },
  { id: 'kb-wbc', displayName: 'Лейкоциты', specimenKbId: 'sp-blood', specimenDisplayName: 'Кровь', preview: 'Белые клетки крови, защищают от инфекций.', verificationStatus: 1, verificationStale: false, updatedAt: '2026-07-01T12:00:00Z' },
  { id: 'kb-crp', displayName: 'С-реактивный белок', specimenKbId: 'sp-serum', specimenDisplayName: 'Сыворотка крови', preview: null, verificationStatus: 0, verificationStale: false, updatedAt: '2026-10-14T06:38:00Z' },
  { id: 'kb-protein-urine', displayName: 'Белок в моче', specimenKbId: 'sp-urine', specimenDisplayName: 'Моча', preview: 'В норме почти не определяется.', verificationStatus: 0, verificationStale: false, updatedAt: '2026-09-02T08:00:00Z' },
];

const MEDICATIONS = [
  { id: 'kb-ibu', displayName: 'Ибупрофен', specimenKbId: null, specimenDisplayName: null, preview: 'Обезболивающее и жаропонижающее.', verificationStatus: 1, verificationStale: false, updatedAt: '2026-10-13T09:47:00Z' },
  { id: 'kb-nurofen', displayName: 'Нурофен', specimenKbId: null, specimenDisplayName: null, preview: 'Ибупрофен 200 мг.', verificationStatus: 2, verificationStale: false, updatedAt: '2026-09-01T00:00:00Z' },
  { id: 'kb-omeprazol', displayName: 'Омепразол', specimenKbId: null, specimenDisplayName: null, preview: 'Снижает кислотность желудка.', verificationStatus: 0, verificationStale: false, updatedAt: '2026-08-20T00:00:00Z' },
  { id: 'kb-d3', displayName: 'Аквадетрим', specimenKbId: null, specimenDisplayName: null, preview: 'Витамин D3 в каплях.', verificationStatus: 0, verificationStale: false, updatedAt: '2026-10-14T05:03:00Z' },
  { id: 'kb-sumatr', displayName: 'Суматриптан', specimenKbId: null, specimenDisplayName: null, preview: null, verificationStatus: 3, verificationStale: true, updatedAt: '2026-05-11T00:00:00Z' },
  { id: 'kb-amox', displayName: 'Амоксиклав', specimenKbId: null, specimenDisplayName: null, preview: 'Антибиотик (амоксициллин + клавулановая кислота).', verificationStatus: 0, verificationStale: false, updatedAt: '2026-10-12T13:20:00Z' },
];

const analyteDetail = (id) => {
  const a = ANALYTES.find((x) => x.id === id) ?? ANALYTES[1];
  return {
    id: a.id, normalizedName: a.displayName.toLowerCase(), specimenKbId: a.specimenKbId, specimenDisplayName: a.specimenDisplayName,
    displayName: a.displayName, payloadJson: ANALYTE_PAYLOAD, source: 'ai', aliases: ['ферритин сыворотки', 'ferritin'],
    lockedFields: ['displayName'], payloadVersion: 4, createdAt: '2026-04-20T10:00:00Z', updatedAt: a.updatedAt,
    verificationStatus: a.verificationStatus, verifiedAt: a.verificationStatus ? '2026-09-28T10:00:00Z' : null, verificationStale: a.verificationStale,
  };
};

const medicationDetail = (id) => {
  const m = MEDICATIONS.find((x) => x.id === id) ?? MEDICATIONS[0];
  return {
    id: m.id, normalizedName: m.displayName.toLowerCase(), displayName: m.displayName, payloadJson: MEDICATION_PAYLOAD, source: 'ai',
    aliases: ['ибупрофен 200 мг'], lockedFields: [], payloadVersion: 3, createdAt: '2026-04-20T10:00:00Z', updatedAt: m.updatedAt,
    verificationStatus: m.verificationStatus, verifiedAt: m.verificationStatus ? '2026-10-13T09:47:00Z' : null, verificationStale: m.verificationStale,
  };
};

const VERIFICATION = {
  labAnalytes: { total: 412, aiUnverified: 287, adminVerified: 84, adminEdited: 29, manualKnowledge: 12, verified: 125 },
  medications: { total: 156, aiUnverified: 61, adminVerified: 70, adminEdited: 18, manualKnowledge: 7, verified: 95 },
};

// ------------------------------------------------------------------ История правок

const HISTORY = [
  { id: 'h1', at: T(14, '09:12'), actor: 'admin', target: 0, targetId: 'kb-ttg', targetLabel: 'Тиреотропный гормон (ТТГ)', action: 'edit', note: 'Поправил нижнюю границу нормы', revertedLogId: null, canRevert: true },
  { id: 'h2', at: T(13, '21:40'), actor: 'system', target: 2, targetId: 'sc-ferritin', targetLabel: 'ферритин · Сыворотка крови', action: 'search-refresh', note: null, revertedLogId: null, canRevert: false },
  { id: 'h3', at: T(13, '09:47'), actor: 'admin', target: 1, targetId: 'kb-ibu', targetLabel: 'Ибупрофен', action: 'verify', note: null, revertedLogId: null, canRevert: false },
  { id: 'h4', at: T(12, '18:05'), actor: 'admin', target: 1, targetId: 'kb-nurofen', targetLabel: 'Нурофен', action: 'revert', note: 'Откат ошибочной правки', revertedLogId: 'h7', canRevert: false },
  { id: 'h5', at: T(12, '13:20'), actor: 'system', target: 1, targetId: 'kb-amox', targetLabel: 'Амоксиклав', action: 'create', note: null, revertedLogId: null, canRevert: true },
  { id: 'h6', at: T(11, '10:02'), actor: 'admin', target: 3, targetId: 'sc-ibu', targetLabel: 'ибупрофен', action: 'snippet-pin', note: 'Закрепил ГРЛС', revertedLogId: null, canRevert: true },
];

// ------------------------------------------------------------------ Журнал вызовов

const call = (id, occurredAt, provider, topic, normalizedName, specimen, httpStatus, durationMs, outcome, snippetCount, jobKind) => ({
  id, occurredAt, provider, topic, normalizedName, specimenDisplayName: specimen, httpStatus, durationMs, outcome, snippetCount,
  jobKind, jobId: jobKind ? `j-${id}` : null,
});

const SEARCH_CALLS = [
  call('c1', T(14, '09:38'), 'yandex', 1, 'с-реактивный белок', 'Сыворотка крови', 200, 1240, 0, 8, 'lab-analyte'),
  call('c2', T(14, '08:02'), 'yandex', 1, 'ферритин', 'Сыворотка крови', 200, 980, 3, 6, 'lab-analyte'),
  call('c3', T(14, '07:41'), 'yandex', 0, 'омез', null, 503, 312, 4, 0, 'medication'),
  call('c4', T(13, '22:15'), 'cache', 0, 'ибупрофен', null, null, 4, 6, 5, 'medication'),
  call('c5', T(13, '18:01'), 'yandex', 1, 'гемоглобин', 'Кровь', 200, 1105, 0, 7, 'lab-analyte'),
  call('c6', T(13, '12:47'), 'brave', 0, 'аквамарис', null, 200, 2210, 1, 0, 'visit-medication'),
  call('c7', T(13, '03:00'), 'yandex', 1, '25-oh витамин d', 'Сыворотка крови', null, 15000, 5, 0, 'warmup'),
];

const SEARCH_CALL_STATS = {
  totalCalls: 1284, paidCalls: 731, cacheHits: 553, cacheHitShare: 0.43,
  byProvider: [{ key: 'yandex', count: 612 }, { key: 'brave', count: 119 }, { key: 'cache', count: 553 }],
  byOutcome: [{ key: 'Ok', count: 642 }, { key: 'CacheHit', count: 553 }, { key: 'Empty', count: 41 }, { key: 'NoUsedSources', count: 28 }, { key: 'HttpError', count: 13 }, { key: 'Timeout', count: 7 }],
  byDay: Array.from({ length: 14 }, (_, i) => ({ day: `2026-10-${String(i + 1).padStart(2, '0')}`, paidCalls: [42, 38, 55, 61, 12, 9, 47, 58, 64, 51, 33, 70, 49, 18][i], cacheHits: [30, 28, 41, 35, 8, 6, 39, 44, 52, 40, 29, 61, 47, 13][i] })),
  usedThisMonth: 731, webSearchPaused: false, pausedAt: null, estimatedMonthlySpend: 584.8,
};

// ------------------------------------------------------------------ Прогрев / пересборки

const WARMUP_STATUS = {
  runId: 'w-3', status: 'Completed', topic: 1, specimenDisplayName: 'Сыворотка крови', totalNames: 40, cursor: 40, paidCalls: 23,
  skippedKbHit: 11, skippedFreshCache: 4, failures: 2, maxPaidCalls: 30, startedAt: T(13, '02:00'), finishedAt: T(13, '02:41'), lastError: null,
};
const WARMUP_HISTORY = [
  { ...WARMUP_STATUS, names: ['ферритин', 'ттг', 'т4 свободный', 'витамин b12'] },
  { runId: 'w-2', status: 'Failed', topic: 0, specimenDisplayName: null, totalNames: 25, cursor: 9, paidCalls: 8, skippedKbHit: 1, skippedFreshCache: 0,
    failures: 5, maxPaidCalls: 20, startedAt: T(10, '01:00'), finishedAt: T(10, '01:12'), lastError: 'Яндекс.Поиск: 429 Too Many Requests', names: ['нурофен', 'омез', 'аквадетрим'] },
  { runId: 'w-1', status: 'Cancelled', topic: 1, specimenDisplayName: 'Моча', totalNames: 12, cursor: 5, paidCalls: 5, skippedKbHit: 0, skippedFreshCache: 0,
    failures: 0, maxPaidCalls: null, startedAt: T(5, '22:30'), finishedAt: T(5, '22:38'), lastError: null, names: ['белок в моче', 'лейкоциты в моче'] },
];

const rebuild = (runId, status, startedAt, finishedAt, extra = {}) => ({
  runId, status, startedAt, finishedAt, lastError: null, stageIndex: 5, cacheMerged: 48, indicatorsUpdated: 1312, indicatorsMerged: 37,
  catalogDeleted: 12, reseedRequested: 64, attempts: 1, ...extra,
});
const REBUILD_HISTORY = [
  rebuild('rb-3', 'Completed', T(12, '23:00'), T(12, '23:18')),
  rebuild('rb-2', 'Failed', T(9, '23:00'), T(9, '23:04'), { stageIndex: 2, cacheMerged: 12, indicatorsUpdated: 0, indicatorsMerged: 0, catalogDeleted: 0, reseedRequested: 0, attempts: 3, lastError: 'Npgsql.PostgresException: 40P01 deadlock detected' }),
  rebuild('rb-1', 'Completed', '2026-09-30T23:00:00+03:00', '2026-09-30T23:21:00+03:00'),
];

// ------------------------------------------------------------------ Настройки

const PIPELINE_STEPS = [
  ['lab-analyte', 'normalize', 'Нормализация названия и биоматериала', true, true, null, null],
  ['lab-analyte', 'web-search', 'Платный веб-поиск по доверенным доменам', true, true, null, null],
  ['lab-analyte', 'legitimacy', 'Проверка, что это реальный показатель', false, true, 'lab-analyte.legitimacy', null],
  ['lab-analyte', 'summarize', 'Сборка статьи справочника', true, true, 'lab-analyte.summarize', null],
  ['lab-analyte', 'plausibility', 'Проверка правдоподобия референсов', false, false, 'lab-analyte.plausibility', T(8, '17:20')],
  ['medication', 'web-search', 'Платный веб-поиск по доверенным доменам', true, true, null, null],
  ['medication', 'summarize', 'Сборка статьи о препарате', true, true, 'medication.summarize', null],
  ['medication', 'driving', 'Оценка влияния на вождение', false, true, 'medication.driving', T(2, '11:00')],
  ['extraction', 'ocr', 'Распознавание текста PDF/фото', true, true, null, null],
  ['extraction', 'extract', 'Извлечение показателей из документа', true, true, 'extraction.indicators', null],
].map(([pipelineKey, stepKey, description, isMandatory, isEnabled, promptKey, updatedAt]) =>
  ({ pipelineKey, stepKey, description, isMandatory, isEnabled, promptKey, updatedAt }));

const PROMPT_SLOTS = [
  { key: 'extraction.indicators', description: 'Извлечение показателей из текста анализа', activeVersion: 7, activeVersionCreatedAt: T(9, '15:00') },
  { key: 'lab-analyte.summarize', description: 'Статья справочника показателя по сниппетам', activeVersion: 4, activeVersionCreatedAt: '2026-09-21T12:00:00+03:00' },
  { key: 'lab-analyte.legitimacy', description: 'Проверка, что название — лабораторный показатель', activeVersion: null, activeVersionCreatedAt: null },
  { key: 'lab-analyte.plausibility', description: 'Проверка правдоподобия референсных диапазонов', activeVersion: 2, activeVersionCreatedAt: '2026-08-30T10:00:00+03:00' },
  { key: 'medication.summarize', description: 'Статья о препарате по сниппетам', activeVersion: 3, activeVersionCreatedAt: '2026-09-02T09:30:00+03:00' },
  { key: 'medication.driving', description: 'Влияние препарата на вождение', activeVersion: null, activeVersionCreatedAt: null },
];

const promptVersions = (key) => {
  const slot = PROMPT_SLOTS.find((s) => s.key === key);
  const active = slot?.activeVersion ?? 0;
  return Array.from({ length: Math.max(active, 1) }, (_, i) => active - i).filter((v) => v > 0).slice(0, 4).map((version) => ({
    id: `${key}-v${version}`, version, isActive: version === active,
    note: version === active ? 'Уточнил формат refRanges' : version === active - 1 ? 'Добавил запрет на выдуманные нормы' : null,
    createdAt: `2026-09-${String(20 - version).padStart(2, '0')}T12:00:00+03:00`,
    body: 'Ты — медицинский редактор. По сниппетам ниже составь статью справочника о показателе «{name}» '
      + '({specimen}). Используй только факты из сниппетов, ссылайся на номер источника.\n\nОтвет — строго JSON по схеме:\n'
      + '{ "plainExplanation": "...", "whyMeasured": "...", "highMeans": "...", "lowMeans": "...", "refRanges": [ ... ] }',
  }));
};

const CONFIG = {
  sections: [
    { name: 'LmStudio', title: 'LM Studio', items: [
      { key: 'LmStudio:BaseUrl', value: 'http://lmstudio:1234/v1', isSecret: false, isSet: true, source: 'env' },
      { key: 'LmStudio:Model', value: 'qwen2.5-14b-instruct', isSecret: false, isSet: true, source: 'appsettings' },
      { key: 'LmStudio:TimeoutSeconds', value: '120', isSecret: false, isSet: false, source: 'default' },
      { key: 'LmStudio:ApiKey', value: null, isSecret: true, isSet: false, source: 'default' },
    ] },
    { name: 'WebSearch', title: 'Веб-поиск', items: [
      { key: 'WebSearch:Provider', value: 'yandex', isSecret: false, isSet: true, source: 'env' },
      { key: 'WebSearch:PricePerPaidCall', value: '0.8', isSecret: false, isSet: true, source: 'env' },
      { key: 'WebSearch:Yandex:ApiKey', value: null, isSecret: true, isSet: true, source: 'env' },
      { key: 'WebSearch:Brave:ApiKey', value: null, isSecret: true, isSet: true, source: 'env' },
    ] },
    { name: 'Storage', title: 'Хранилище (MinIO)', items: [
      { key: 'Minio:Endpoint', value: 'minio:9000', isSecret: false, isSet: true, source: 'env' },
      { key: 'Minio:Bucket', value: 'familyhub-attachments', isSecret: false, isSet: true, source: 'appsettings' },
      { key: 'Minio:SecretKey', value: null, isSecret: true, isSet: true, source: 'env' },
    ] },
    { name: 'Telegram', title: 'Telegram-бот', items: [
      { key: 'Telegram:BotUsername', value: 'familyhub_bot', isSecret: false, isSet: true, source: 'env' },
      { key: 'Telegram:BotToken', value: null, isSecret: true, isSet: true, source: 'env' },
      { key: 'Telegram:WebhookSecret', value: null, isSecret: true, isSet: false, source: 'default' },
    ] },
  ],
};

// ------------------------------------------------------------------ Кэш поиска

const cacheRow = (id, normalizedName, displayName, specimen, provider, lastUpdatedAt, snippetCount, units) => ({
  id, normalizedName, displayName, specimen, provider, lastUpdatedAt,
  canBeUpdatedAfter: new Date(new Date(lastUpdatedAt).getTime() + 90 * 864e5).toISOString(), snippetCount, units,
});
const SEARCH_CACHE = {
  0: [
    cacheRow('sc-ibu', 'ибупрофен', 'Ибупрофен', null, 'yandex', T(13, '22:15'), 5, null),
    cacheRow('sc-nurofen', 'нурофен', 'Нурофен', null, 'yandex', '2026-09-01T10:00:00+03:00', 6, null),
    cacheRow('sc-omez', 'омез', null, null, 'yandex', T(14, '07:41'), 0, null),
    cacheRow('sc-aqua', 'аквадетрим', 'Аквадетрим', null, 'brave', '2026-07-10T12:00:00+03:00', 4, null),
    cacheRow('sc-amox', 'амоксиклав', 'Амоксиклав', null, 'yandex', T(12, '13:15'), 7, null),
  ],
  1: [
    cacheRow('sc-ferritin', 'ферритин', 'Ферритин', 'Сыворотка крови', 'yandex', T(12, '14:20'), 4, 'мкг/л; нг/мл'),
    cacheRow('sc-hb-blood', 'гемоглобин', 'Гемоглобин', 'Кровь', 'yandex', T(2, '10:00'), 5, 'г/л'),
    cacheRow('sc-ttg', 'ттг', 'ТТГ', 'Сыворотка крови', 'yandex', '2026-06-18T09:00:00+03:00', 6, null),
    cacheRow('sc-crp', 'с-реактивный белок', 'СРБ', 'Сыворотка крови', 'yandex', T(14, '09:38'), 8, ''),
    cacheRow('sc-urine-protein', 'белок в моче', null, 'Моча', 'brave', '2026-05-02T09:00:00+03:00', 3, 'г/л'),
  ],
};

// ------------------------------------------------------------------ Маршруты

/** [метод, регулярка пути, ответ | (match, query, body) => ответ] — подмешиваются в ROUTES fixtures.mjs. */
export const ADMIN_ROUTES = [
  ['GET', /^\/api\/admin\/session$/, { status: 200, body: {} }],

  // Мониторинг
  ['GET', /^\/api\/admin\/stats\/overview$/, {
    users: { total: 148, telegramOnly: 37, pwaOnly: 71, both: 40, newLast7Days: 6, newLast30Days: 23, lockedOut: 2 },
    families: { total: 52, withActiveMembers: 47, averageActiveMembers: 2.8 },
    domain: { medicalRecords: 1934, medications: 611, expiredMedications: 44, birthdays: 318, attachments: 2471, familyDependents: 29 },
  }],
  ['GET', /^\/api\/admin\/stats\/storage$/, {
    bucketSizeBytes: 7_834_512_384, bucketObjectCount: 2486, attachmentsSizeBytesInDb: 7_801_223_168, attachmentsCountInDb: 2471,
    reconciliation: { orphanedBlobs: 15, brokenAttachments: 2 }, computedAt: T(14, '09:15'),
  }],
  ['GET', /^\/api\/admin\/stats\/system$/, {
    outbox: { undeliveredBatches: 3, oldestUndeliveredAt: T(14, '09:21') },
    hangfireQueues: [
      { name: 'default', enqueuedCount: 2 }, { name: 'enrichment', enqueuedCount: 14 },
      { name: 'extraction', enqueuedCount: 1 }, { name: 'notifications', enqueuedCount: 0 },
    ],
    hangfireFailedJobsTotal: 7, postgresHealthy: true, minioHealthy: true, kafkaHealthy: false,
  }],

  // Безопасность
  ['GET', /^\/api\/admin\/stats\/security$/, {
    encryptionDistribution: {
      fieldValues: [{ keyId: 'k-2026-09', count: 18342 }, { keyId: 'k-2026-03', count: 1207 }],
      attachmentBlobs: [{ keyId: 'k-2026-09', count: 2210 }, { keyId: 'k-2026-03', count: 261 }],
    },
    crossUserMedicalAccessLast30Days: 3, usersWithoutCurrentConsent: 11, activeSessions: 94,
    dataProtectionKeyCount: 6, oldestDataProtectionKeyCreatedAt: '2026-01-14T10:00:00Z',
  }],
  ['GET', /^\/api\/admin\/keys$/, {
    encryption: { activeKeyId: 'k-2026-09', previousKeyIds: ['k-2026-03', 'k-2025-09'] },
    jwt: { activeKeyId: 'jwt-2026-10', previousKeyIds: ['jwt-2026-07'] },
    attachments: { previousKeyCount: 1 },
  }],
  ['GET', /^\/api\/admin\/keys\/encryption\/rotate\/status$/, {
    runId: 'rot-5', targetKeyId: 'k-2026-09', status: 'Failed', startedAt: T(11, '02:00'), finishedAt: T(11, '02:37'),
    lastError: 'Blob attachments/2f/9a1c… не найден в MinIO (404) — пропущен, остальное перешифровано.',
    fieldsProcessed: 19549, fieldsTotal: 19549, blobsProcessed: 2470, blobsTotal: 2471,
  }],
  ['GET', /^\/api\/admin\/credentials$/, {
    postgres: {
      mode: 'LeastPrivilege', sessionUser: 'familyhub_app_b',
      slots: [
        { role: 'familyhub_app_a', canLogin: true, hasPassword: true, activeSessions: 2 },
        { role: 'familyhub_app_b', canLogin: true, hasPassword: true, activeSessions: 18 },
      ],
      currentSince: T(1, '12:00'), pending: null, revocableOld: 'familyhub_app_a',
    },
    minio: {
      mode: 'ServiceAccount', accessKeyMasked: 'FH8K••••••••Q2LM', currentSince: '2026-08-15T10:00:00+03:00',
      pending: { id: 'cr-9', kind: 'Minio', from: 'FH8K••••Q2LM', to: 'FH3Z••••W7PA', status: 'AwaitingDeploy',
        generatedAt: T(13, '17:05'), generatedBy: 'admin', activatedAt: null, revokedAt: null, terminatedSessions: null },
      revocableOldMasked: null,
    },
    history: [
      { id: 'cr-9', kind: 'Minio', from: 'FH8K••••Q2LM', to: 'FH3Z••••W7PA', status: 'AwaitingDeploy', generatedAt: T(13, '17:05'), generatedBy: 'admin', activatedAt: null, revokedAt: null, terminatedSessions: null },
      { id: 'cr-8', kind: 'Postgres', from: 'familyhub_app_a', to: 'familyhub_app_b', status: 'Activated', generatedAt: T(1, '11:40'), generatedBy: 'admin', activatedAt: T(1, '12:00'), revokedAt: null, terminatedSessions: null },
      { id: 'cr-7', kind: 'Postgres', from: 'familyhub_app_b', to: 'familyhub_app_a', status: 'Revoked', generatedAt: '2026-07-02T10:00:00+03:00', generatedBy: 'admin', activatedAt: '2026-07-02T10:30:00+03:00', revokedAt: '2026-07-03T09:00:00+03:00', terminatedSessions: 4 },
      { id: 'cr-6', kind: 'Minio', from: 'FH1A••••K0ZZ', to: 'FH8K••••Q2LM', status: 'Superseded', generatedAt: '2026-08-15T09:40:00+03:00', generatedBy: 'admin', activatedAt: '2026-08-15T10:00:00+03:00', revokedAt: null, terminatedSessions: null },
    ],
  }],

  // Настройки
  ['GET', /^\/api\/admin\/config$/, CONFIG],
  ['GET', /^\/api\/admin\/lmstudio\/model$/, { activeModel: 'qwen2.5-14b-instruct', fallbackModel: 'llama-3.1-8b-instruct' }],
  ['GET', /^\/api\/admin\/lmstudio\/available-models$/, { lmStudioReachable: true, models: ['qwen2.5-14b-instruct', 'qwen2.5-7b-instruct', 'llama-3.1-8b-instruct', 'mistral-nemo-12b'] }],
  ['GET', /^\/api\/admin\/lmstudio\/reasoning$/, { activeReasoning: 2, fallbackReasoning: 0 }],
  ['GET', /^\/api\/admin\/pipeline\/pipelines$/, PIPELINE_STEPS],
  ['GET', /^\/api\/admin\/pipeline\/prompts$/, PROMPT_SLOTS],
  ['GET', /^\/api\/admin\/pipeline\/prompts\/([^/]+)\/versions$/, (m) => promptVersions(decodeURIComponent(m[1]))],
  ['GET', /^\/api\/admin\/enrichment\/web-search$/, { isPaused: false, pausedAt: null, note: 'Лимит гранта — 1000 вызовов в месяц' }],
  ['GET', /^\/api\/admin\/enrichment\/trusted-domains$/, (_m, q) => (q.get('topic') === '1' ? TRUSTED_DOMAINS_LAB : TRUSTED_DOMAINS_MED)],

  // Требует внимания / Одобрение
  ['GET', /^\/api\/admin\/pipeline\/attention$/, ATTENTION],
  ['GET', /^\/api\/admin\/review\/counts$/, {
    searches: 4, results: 3,
    searchesByKind: { 'lab-analyte': 3, medication: 1 }, resultsByKind: { 'lab-analyte': 1, medication: 1, 'visit-medication': 1 },
  }],
  ['GET', /^\/api\/admin\/review\/inbox$/, (_m, q) => {
    const rows = REVIEW_ROWS.filter((r) => (!q.get('kind') || r.kind === q.get('kind')) && (!q.get('stage') || r.stage === q.get('stage')));
    return { rows, total: rows.length, searches: rows.filter((r) => r.stage === 'search').length, results: rows.filter((r) => r.stage === 'result').length };
  }],
  ['GET', /^\/api\/admin\/review\/items\/([^/]+)\/([^/]+)$/, (m) => reviewItem(m[2], m[1])],
  ['GET', /^\/api\/admin\/review\/items\/([^/]+)\/([^/]+)\/cache-candidates$/, []],
  ['GET', /^\/api\/admin\/review\/entity\/([^/]+)\/([^/]+)$/, (m) => ({
    kind: m[1], current: CURRENT_FERRITIN, specimen: 'Сыворотка крови', cache: reviewCache(m[1] === 'lab-analyte' ? 'lab-analyte' : 'medication'),
    sources: CACHE_SNIPPETS_FERRITIN.map((s, i) => reviewSource(i, s)), queueJobId: null, queueStage: null,
  })],
  ['GET', /^\/api\/admin\/review\/config$/, {
    medicationQueryMinConfidence: 0.6, analyteQueryMinConfidence: 0.7, medicationResultMinConfidence: 0.8, analyteResultMinConfidence: 0.75,
    updatedAt: '2026-09-30T18:00:00+03:00',
  }],
  ['GET', /^\/api\/admin\/history$/, (_m, q) => {
    const names = ['LabAnalyteKb', 'MedicationKb', 'LabAnalyteSearchCache', 'MedicationSearchCache'];
    const target = q.get('target');
    const items = HISTORY.filter((h) => (!target || names[h.target] === target) && (!q.get('targetId') || h.targetId === q.get('targetId')));
    return { items, total: items.length };
  }],
  ['GET', /^\/api\/admin\/history\/([^/]+)$/, (m) => {
    const h = HISTORY.find((x) => x.id === m[1]) ?? HISTORY[0];
    const { canRevert: _c, ...rest } = h;
    return { ...rest, beforeJson: '{"displayName":"ТТГ","refRanges":[{"low":0.4,"high":4}]}', afterJson: '{"displayName":"Тиреотропный гормон (ТТГ)","refRanges":[{"low":0.27,"high":4.2}]}' };
  }],

  // Операции
  ['GET', /^\/api\/admin\/pipeline\/jobs$/, (_m, q) => {
    let rows = JOBS[q.get('type') ?? 'lab-analyte'] ?? [];
    if (q.get('status')) rows = rows.filter((j) => j.status === q.get('status'));
    if (q.get('reason')) rows = rows.filter((j) => j.failureReason === q.get('reason'));
    return { rows, total: rows.length };
  }],
  ['GET', /^\/api\/admin\/pipeline\/jobs\/([^/]+)$/, (m, q) => jobDetail(m[1], q.get('type'))],
  ['GET', /^\/api\/admin\/enrichment\/warmup\/status$/, WARMUP_STATUS],
  ['GET', /^\/api\/admin\/enrichment\/warmup\/history$/, WARMUP_HISTORY],
  ['GET', /^\/api\/admin\/enrichment\/search-cache$/, (_m, q) => {
    const rows = SEARCH_CACHE[q.get('topic') === '1' ? 1 : 0];
    return { rows, total: rows.length };
  }],
  ['GET', /^\/api\/admin\/enrichment\/search-cache\/([^/]+)$/, (m) => ({ ...CACHE_DETAIL_FERRITIN, id: m[1] })],
  ['GET', /^\/api\/admin\/kb\/lab-analytes\/rebuild\/status$/, REBUILD_HISTORY[0]],
  ['GET', /^\/api\/admin\/kb\/lab-analytes\/rebuild\/history$/, REBUILD_HISTORY],
  ['GET', /^\/api\/admin\/pipeline\/batch-status$/, {
    jobs: [
      { key: 'cache-units', remaining: 37, total: 412, active: true },
      { key: 'indicator-flags', remaining: 0, total: 1934, active: false },
      { key: 'reenrich', remaining: 118, total: 412, active: false },
    ],
    checkedAt: T(14, '09:39'),
  }],
  ['GET', /^\/api\/admin\/search-calls$/, (_m, q) => ({ rows: SEARCH_CALLS, total: SEARCH_CALLS.length, page: Number(q.get('page') ?? 1), pageSize: Number(q.get('pageSize') ?? 50) })],
  ['GET', /^\/api\/admin\/search-calls\/stats$/, SEARCH_CALL_STATS],
  ['GET', /^\/api\/admin\/search-calls\/([^/]+)$/, (m) => {
    const c = SEARCH_CALLS.find((x) => x.id === m[1]) ?? SEARCH_CALLS[0];
    return { ...c, queryText: `${c.normalizedName} норма ${c.specimenDisplayName ?? 'инструкция'}`, endpoint: 'https://searchapi.api.cloud.yandex.net/v2/web/search',
      resultUrls: ['https://invitro.ru/analizes/for-doctors/', 'https://helix.ru/kb/'], error: c.outcome === 4 ? '503 Service Unavailable' : null };
  }],

  // Справочник
  ['GET', /^\/api\/admin\/kb\/verification-summary$/, VERIFICATION],
  ['GET', /^\/api\/admin\/kb\/lab-analytes$/, { items: ANALYTES, hasMore: false }],
  ['GET', /^\/api\/admin\/kb\/lab-analytes\/([^/]+)$/, (m) => analyteDetail(m[1])],
  ['POST', /^\/api\/admin\/kb\/lab-analytes\/resolve-related$/, (_m, _q, body) => (Array.isArray(body) ? body : []).map((name) => {
    const a = ANALYTES.find((x) => x.displayName.toLowerCase() === String(name).toLowerCase());
    return { name, id: a?.id ?? null, displayName: a?.displayName ?? null, specimenDisplayName: a?.specimenDisplayName ?? null };
  })],
  ['GET', /^\/api\/admin\/kb\/medications$/, { items: MEDICATIONS, hasMore: false }],
  ['GET', /^\/api\/admin\/kb\/medications\/([^/]+)$/, (m) => medicationDetail(m[1])],
  ['GET', /^\/api\/admin\/kb\/specimens\/search-groups$/, SPECIMEN_GROUPS],
  ['GET', /^\/api\/admin\/kb\/specimens$/, SPECIMENS],
];
