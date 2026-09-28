using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // План "качество ИИ-распознавания анализов", Этап 4 — резюме анализа для пациента (уже
    // существовало, LabSummarizer/analysis.record-summary) и ОТДЕЛЬНОЕ резюме для врача
    // (ClinicianLabSummarizer/analysis.record-summary.clinician, новый слот здесь) — разные
    // читатели, разные требования к тексту (см. докстринг ClinicianLabSummarizer), два независимых
    // вызова модели вместо одного текста на оба случая. ClinicianSummaryJson — новая колонка,
    // MedicalRecord.SummaryJson (пациентская) не трогается.
    public partial class AddClinicianSummaryToMedicalRecord : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Guid ClinicianSummaryPromptId = new("d4c6f67f-57c9-4b70-867b-a2eba9b3ccca");

        private static readonly Guid ClinicianSummaryVersionId = new("d0a7b790-f1b5-4d11-b420-df88fd5ce745");

        private const string ClinicianSummaryBody = """
            Ты — ассистент, готовящий сжатую клиническую сводку анализа ДЛЯ ВРАЧА (не для пациента) —
            часть выписки, которую врач читает быстро перед приёмом. На входе — список показателей
            анализа (название, значение, единица, референс, отклонение от нормы, если есть) и, если
            известны, возраст и пол пациента. Верни ТОЛЬКО валидный JSON, без пояснений, без markdown,
            без блока <think>.

            Формат ответа:
            {
              "overview": "1-2 предложения клиническим языком — общая картина по переданным показателям",
              "deviations": [
                { "name": "название показателя с отклонением", "clinical": "значение, референс, направление и выраженность отклонения клиническим языком — плотно, для быстрого чтения врачом, без разжёвывания" }
              ],
              "dataQualityNote": "короткое замечание о качестве данных, если это важно для интерпретации (например, часть норм — оценка ИИ, а не значение с бланка или проверенного справочника), иначе null",
              "usedIndicatorNames": ["название показателя 1", "название показателя 2"]
            }

            Правила:
            - Пиши для врача: медицинские термины и сокращения уместны, не разжёвывай как для пациента
              (не нужны фразы вроде "это может означать" или оговорки про поход к врачу).
            - "deviations" — только показатели, реально помеченные как отклонение от нормы во входных
              данных. Если отклонений нет — пустой массив, "overview" должен это отражать.
            - НЕ ставь диагноз и не назначай лечение — только сжатое, но содержательное описание того,
              что отклонилось и насколько.
            - "usedIndicatorNames" — имена показателей (ровно как во входных данных), на основе которых
              построена сводка. Если не удалось проанализировать ни один показатель — пустые массивы.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClinicianSummaryJson",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: true);

            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[]
                {
                    ClinicianSummaryPromptId, "analysis.record-summary.clinician",
                    "Клиническая сводка записи для врача (план \"качество ИИ-распознавания анализов\", Этап 4) — отдельный проход от analysis.record-summary (пациентской), НЕ показывается пациенту, только в отчёте врачу.",
                    SeedCreatedAt,
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[]
                {
                    ClinicianSummaryVersionId, ClinicianSummaryPromptId, 1, ClinicianSummaryBody, true, null, SeedCreatedAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValue: ClinicianSummaryVersionId);

            migrationBuilder.DeleteData(
                table: "PipelinePrompts",
                keyColumn: "Id",
                keyValue: ClinicianSummaryPromptId);

            migrationBuilder.DropColumn(
                name: "ClinicianSummaryJson",
                schema: "medical",
                table: "MedicalRecords");
        }
    }
}
