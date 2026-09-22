using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_classification_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    AiModel = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_classification_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_classification_logs_email_messages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "email_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_model_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ModelIdentifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    TaskCapability = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    FallbackOrder = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationMetadata = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_model_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "classification_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Categories = table.Column<string>(type: "text", nullable: false),
                    IncludeDefinitions = table.Column<string>(type: "text", nullable: false),
                    ExcludeDefinitions = table.Column<string>(type: "text", nullable: false),
                    ExampleSubject = table.Column<string>(type: "text", nullable: true),
                    ExampleContent = table.Column<string>(type: "text", nullable: true),
                    ExpectedClassification = table.Column<string>(type: "text", nullable: true),
                    HighConfidenceThreshold = table.Column<double>(type: "double precision", nullable: true),
                    MediumConfidenceThreshold = table.Column<double>(type: "double precision", nullable: true),
                    TreatMediumConfidenceAsReviewRequired = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classification_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "email_classifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClassificationProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeterministicFilterMatched = table.Column<bool>(type: "boolean", nullable: false),
                    DeterministicFilterReason = table.Column<string>(type: "text", nullable: true),
                    Relevance = table.Column<int>(type: "integer", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: true),
                    ActionRequired = table.Column<bool>(type: "boolean", nullable: true),
                    ResponseExpected = table.Column<bool>(type: "boolean", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: true),
                    AiConfidence = table.Column<double>(type: "double precision", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    AiProvider = table.Column<string>(type: "text", nullable: true),
                    AiModel = table.Column<string>(type: "text", nullable: true),
                    PromptProfileVersion = table.Column<string>(type: "text", nullable: true),
                    ProcessingDurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ProcessingError = table.Column<string>(type: "text", nullable: true),
                    ConfidenceBand = table.Column<int>(type: "integer", nullable: true),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    DecisionReason = table.Column<string>(type: "text", nullable: true),
                    IsManuallyCorrected = table.Column<bool>(type: "boolean", nullable: false),
                    CorrectedRelevance = table.Column<int>(type: "integer", nullable: true),
                    CorrectedCategory = table.Column<string>(type: "text", nullable: true),
                    CorrectedPriority = table.Column<int>(type: "integer", nullable: true),
                    ClassifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_classifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_email_classifications_classification_profiles_Classificatio~",
                        column: x => x.ClassificationProfileId,
                        principalTable: "classification_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_email_classifications_email_messages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "email_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_classification_logs_EmailMessageId",
                table: "ai_classification_logs",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_configs_Provider_ModelIdentifier",
                table: "ai_model_configs",
                columns: new[] { "Provider", "ModelIdentifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_configs_TaskCapability",
                table: "ai_model_configs",
                column: "TaskCapability");

            migrationBuilder.CreateIndex(
                name: "IX_classification_profiles_Name",
                table: "classification_profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_classifications_ClassificationProfileId",
                table: "email_classifications",
                column: "ClassificationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_email_classifications_Decision",
                table: "email_classifications",
                column: "Decision");

            migrationBuilder.CreateIndex(
                name: "IX_email_classifications_EmailMessageId",
                table: "email_classifications",
                column: "EmailMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_classification_logs");

            migrationBuilder.DropTable(
                name: "ai_model_configs");

            migrationBuilder.DropTable(
                name: "email_classifications");

            migrationBuilder.DropTable(
                name: "classification_profiles");
        }
    }
}
