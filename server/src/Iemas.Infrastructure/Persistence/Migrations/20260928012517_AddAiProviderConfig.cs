using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProviderConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_provider_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EncryptedApiKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    ApiKeyNonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    ApiKeyTag = table.Column<byte[]>(type: "bytea", nullable: true),
                    ApiKeyEncryptionKeyId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ApiKeyHint = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_configs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_provider_configs_Provider",
                table: "ai_provider_configs",
                column: "Provider",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_provider_configs");
        }
    }
}
