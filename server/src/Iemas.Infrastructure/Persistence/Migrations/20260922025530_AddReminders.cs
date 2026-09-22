using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reminder_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    ClassificationProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitialDelay = table.Column<TimeSpan>(type: "interval", nullable: false),
                    ReminderInterval = table.Column<TimeSpan>(type: "interval", nullable: false),
                    MaxReminders = table.Column<int>(type: "integer", nullable: false),
                    MinimumInterval = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RestrictToBusinessHours = table.Column<bool>(type: "boolean", nullable: false),
                    BusinessHoursStart = table.Column<TimeSpan>(type: "interval", nullable: false),
                    BusinessHoursEnd = table.Column<TimeSpan>(type: "interval", nullable: false),
                    ExcludeWeekends = table.Column<bool>(type: "boolean", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExpirationWindow = table.Column<TimeSpan>(type: "interval", nullable: true),
                    EscalationThresholdReminderCount = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "reminder_policy_holidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReminderPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_policy_holidays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reminder_policy_holidays_reminder_policies_ReminderPolicyId",
                        column: x => x.ReminderPolicyId,
                        principalTable: "reminder_policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReminderPolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    ScheduledForUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedForUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceAgentCaseActionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<int>(type: "integer", nullable: true),
                    CancelDetail = table.Column<string>(type: "text", nullable: true),
                    DeliveryAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastFailureDetail = table.Column<string>(type: "text", nullable: true),
                    ExecutionClaimToken = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reminders_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reminders_reminder_policies_ReminderPolicyId",
                        column: x => x.ReminderPolicyId,
                        principalTable: "reminder_policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_policies_ClassificationProfileId",
                table: "reminder_policies",
                column: "ClassificationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_policies_IsDefault",
                table: "reminder_policies",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_policies_Name",
                table: "reminder_policies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reminder_policy_holidays_ReminderPolicyId_Date",
                table: "reminder_policy_holidays",
                columns: new[] { "ReminderPolicyId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reminders_CaseId",
                table: "reminders",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_reminders_ExecutionClaimToken",
                table: "reminders",
                column: "ExecutionClaimToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reminders_ReminderPolicyId",
                table: "reminders",
                column: "ReminderPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_reminders_SourceAgentCaseActionId",
                table: "reminders",
                column: "SourceAgentCaseActionId",
                unique: true,
                filter: "\"SourceAgentCaseActionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reminders_Status_ScheduledForUtc",
                table: "reminders",
                columns: new[] { "Status", "ScheduledForUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminder_policy_holidays");

            migrationBuilder.DropTable(
                name: "reminders");

            migrationBuilder.DropTable(
                name: "reminder_policies");
        }
    }
}
