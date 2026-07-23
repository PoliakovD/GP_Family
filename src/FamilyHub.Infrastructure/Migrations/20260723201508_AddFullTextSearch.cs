using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <summary>
    /// Полнотекстовый поиск (этап 3, ADR-0003): tsvector('russian') + pg_trgm для плейнтекст-данных
    /// (Medications, справочник kb.global_medications_kb). Медкарты (шифрованные поля) сюда НЕ входят —
    /// для них поиск строится in-memory на уровне приложения (см. Modules.Medical/Search), т.к. в
    /// колонках лежит ciphertext (ADR-0002). Только raw SQL: генерируемая колонка search_vector НЕ
    /// заведена в EF-модель — не влияет на SQLite-юнит-тесты (EnsureCreated строит схему из модели,
    /// эту миграцию не выполняет), поиск по ней делается через FromSql/SqlQuery.
    /// </summary>
    public partial class AddFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // --- medical.Medications: название + весь DataJson (инструкция, производитель,
            // дозировка, действующее вещество, OCR-поля) — всё это НЕ персональные данные
            // (ADR-0002: "сведения о препарате семейной аптечки, не о человеке").
            migrationBuilder.Sql("""
                ALTER TABLE medical."Medications"
                    ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        to_tsvector('russian', coalesce("Name", '') || ' ' || coalesce("DataJson"::text, ''))
                    ) STORED;
                """);

            migrationBuilder.Sql(
                """CREATE INDEX "IX_Medications_search_vector" ON medical."Medications" USING GIN (search_vector);""");

            // Trigram-индекс поверх названия — устойчивость к опечаткам OCR (similarity()/% оператор).
            migrationBuilder.Sql(
                """CREATE INDEX "IX_Medications_Name_trgm" ON medical."Medications" USING GIN ("Name" gin_trgm_ops);""");

            // --- kb.global_medications_kb: обезличенный справочник (задача 2.6) — без ограничений.
            migrationBuilder.Sql("""
                ALTER TABLE kb.global_medications_kb
                    ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        to_tsvector('russian',
                            coalesce("DisplayName", '') || ' ' ||
                            coalesce("NormalizedName", '') || ' ' ||
                            coalesce("PayloadJson"::text, ''))
                    ) STORED;
                """);

            migrationBuilder.Sql(
                """CREATE INDEX "IX_global_medications_kb_search_vector" ON kb.global_medications_kb USING GIN (search_vector);""");

            migrationBuilder.Sql(
                """CREATE INDEX "IX_global_medications_kb_DisplayName_trgm" ON kb.global_medications_kb USING GIN ("DisplayName" gin_trgm_ops);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS kb."IX_global_medications_kb_DisplayName_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS kb."IX_global_medications_kb_search_vector";""");
            migrationBuilder.Sql("ALTER TABLE kb.global_medications_kb DROP COLUMN search_vector;");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS medical."IX_Medications_Name_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS medical."IX_Medications_search_vector";""");
            migrationBuilder.Sql("""ALTER TABLE medical."Medications" DROP COLUMN search_vector;""");

            migrationBuilder.Sql("DROP EXTENSION IF EXISTS pg_trgm;");
        }
    }
}
