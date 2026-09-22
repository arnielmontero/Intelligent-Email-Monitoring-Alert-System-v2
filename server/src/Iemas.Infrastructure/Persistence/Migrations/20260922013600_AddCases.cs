using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerEmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    CustomerDisplayName = table.Column<string>(type: "text", nullable: true),
                    OwnerEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    NormalizedSubject = table.Column<string>(type: "text", nullable: false),
                    WorkStatus = table.Column<int>(type: "integer", nullable: false),
                    ReplyStatus = table.Column<int>(type: "integer", nullable: false),
                    NotificationStatus = table.Column<int>(type: "integer", nullable: false),
                    FirstEmailReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletionReason = table.Column<int>(type: "integer", nullable: true),
                    CompletionComment = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReopenCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cases_email_accounts_EmailAccountId",
                        column: x => x.EmailAccountId,
                        principalTable: "email_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cases_employees_OwnerEmployeeId",
                        column: x => x.OwnerEmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_emails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchSignal = table.Column<int>(type: "integer", nullable: false),
                    MatchDetail = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_emails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_case_emails_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_case_emails_email_messages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "email_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    ActorEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_case_events_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_case_emails_CaseId",
                table: "case_emails",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_case_emails_EmailMessageId",
                table: "case_emails",
                column: "EmailMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_case_events_CaseId",
                table: "case_events",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_case_events_OccurredAt",
                table: "case_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_cases_CaseNumber",
                table: "cases",
                column: "CaseNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cases_CustomerEmailAddress",
                table: "cases",
                column: "CustomerEmailAddress");

            migrationBuilder.CreateIndex(
                name: "IX_cases_EmailAccountId_CustomerEmailAddress",
                table: "cases",
                columns: new[] { "EmailAccountId", "CustomerEmailAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_cases_NormalizedSubject",
                table: "cases",
                column: "NormalizedSubject");

            migrationBuilder.CreateIndex(
                name: "IX_cases_OwnerEmployeeId",
                table: "cases",
                column: "OwnerEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_cases_WorkStatus",
                table: "cases",
                column: "WorkStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_emails");

            migrationBuilder.DropTable(
                name: "case_events");

            migrationBuilder.DropTable(
                name: "cases");
        }
    }
}
