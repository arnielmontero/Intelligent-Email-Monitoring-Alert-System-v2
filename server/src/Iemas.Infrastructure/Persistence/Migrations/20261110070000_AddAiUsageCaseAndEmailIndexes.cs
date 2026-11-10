using System;
using Iemas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Shows what AI spent on each email and each inquiry. Resolution checks belong to an inquiry rather than to one
    /// email, so usage records get a nullable CaseId; both it and EmailMessageId are indexed because the case page now
    /// looks costs up by them.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261110070000_AddAiUsageCaseAndEmailIndexes")]
    public partial class AddAiUsageCaseAndEmailIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CaseId",
                table: "ai_usage_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_records_CaseId",
                table: "ai_usage_records",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_records_EmailMessageId",
                table: "ai_usage_records",
                column: "EmailMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_usage_records_EmailMessageId",
                table: "ai_usage_records");

            migrationBuilder.DropIndex(
                name: "IX_ai_usage_records_CaseId",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "CaseId",
                table: "ai_usage_records");
        }
    }
}
