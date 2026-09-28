using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_usage_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ModelIdentifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EmailMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    TotalTokens = table.Column<int>(type: "integer", nullable: true),
                    CostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    GenerationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_records_CreatedAt",
                table: "ai_usage_records",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_records_ModelIdentifier_CreatedAt",
                table: "ai_usage_records",
                columns: new[] { "ModelIdentifier", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage_records");
        }
    }
}
