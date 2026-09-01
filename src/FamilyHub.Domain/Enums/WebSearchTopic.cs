namespace FamilyHub.Domain.Enums;

/// <summary>
/// Какой конвейер обогащения запрашивает поиск — определяет доверенные домены
/// (EnrichmentTrustedDomain.Topic) и формулировку сырого запроса: реестры лекарств (vidal.ru,
/// rlsnet.ru) бесполезны для референсных диапазонов лабораторных показателей, и наоборот (ветка
/// medicalrecords — справочник kb.global_lab_analytes_kb). В Domain (не Infrastructure), потому
/// что на него ссылается доменная сущность EnrichmentTrustedDomain.
/// </summary>
public enum WebSearchTopic { Medication, LabAnalyte }
