using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_intake_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_intake_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_email_intake_logs_email_accounts_EmailAccountId",
                        column: x => x.EmailAccountId,
                        principalTable: "email_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "email_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MessageId = table.Column<string>(type: "text", nullable: true),
                    ThreadId = table.Column<string>(type: "text", nullable: true),
                    InReplyTo = table.Column<string>(type: "text", nullable: true),
                    References = table.Column<string>(type: "text", nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FromDisplayName = table.Column<string>(type: "text", nullable: true),
                    ToAddresses = table.Column<string>(type: "text", nullable: false),
                    CcAddresses = table.Column<string>(type: "text", nullable: true),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: true),
                    BodyHtml = table.Column<string>(type: "text", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttachmentCount = table.Column<int>(type: "integer", nullable: false),
                    ProcessingStatus = table.Column<int>(type: "integer", nullable: false),
                    ProcessingError = table.Column<string>(type: "text", nullable: true),
                    Classification = table.Column<string>(type: "text", nullable: true),
                    AiConfidence = table.Column<double>(type: "double precision", nullable: true),
                    AiModel = table.Column<string>(type: "text", nullable: true),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_email_messages_email_accounts_EmailAccountId",
                        column: x => x.EmailAccountId,
                        principalTable: "email_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "email_sync_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastUidValidity = table.Column<long>(type: "bigint", nullable: true),
                    LastSeenUid = table.Column<long>(type: "bigint", nullable: true),
                    LastSyncStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncError = table.Column<string>(type: "text", nullable: true),
                    ConsecutiveFailureCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_sync_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_email_sync_states_email_accounts_EmailAccountId",
                        column: x => x.EmailAccountId,
                        principalTable: "email_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "email_attachment_metadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_attachment_metadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_email_attachment_metadata_email_messages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "email_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_attachment_metadata_EmailMessageId",
                table: "email_attachment_metadata",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_email_intake_logs_CreatedAt",
                table: "email_intake_logs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_email_intake_logs_EmailAccountId_CreatedAt",
                table: "email_intake_logs",
                columns: new[] { "EmailAccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_EmailAccountId_ProviderMessageId",
                table: "email_messages",
                columns: new[] { "EmailAccountId", "ProviderMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_InReplyTo",
                table: "email_messages",
                column: "InReplyTo");

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_MessageId",
                table: "email_messages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_ProcessingStatus",
                table: "email_messages",
                column: "ProcessingStatus");

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_ReceivedAt",
                table: "email_messages",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_ThreadId",
                table: "email_messages",
                column: "ThreadId");

            migrationBuilder.CreateIndex(
                name: "IX_email_sync_states_EmailAccountId",
                table: "email_sync_states",
                column: "EmailAccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_attachment_metadata");

            migrationBuilder.DropTable(
                name: "email_intake_logs");

            migrationBuilder.DropTable(
                name: "email_sync_states");

            migrationBuilder.DropTable(
                name: "email_messages");
        }
    }
}
