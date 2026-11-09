using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInquirySupervision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyTextEncrypted",
                table: "email_messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Direction",
                table: "email_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConversationSyncedThrough",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EpisodeDeadlineMissedAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpisodeEscalations",
                table: "cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EpisodeLastEscalatedAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EpisodeLastReminderAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpisodeRemindersSent",
                table: "cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EpisodeWaitNoticeAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstAgentResponseAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAgentMessageAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCustomerMessageAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastInternalActivityAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OverdueEscalatedAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OverdueSince",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionAssessedAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolutionAssessedMessageCount",
                table: "cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "ResolutionConfidence",
                table: "cases",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionDueAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionReason",
                table: "cases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolutionVerdict",
                table: "cases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Stage",
                table: "cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StageBeforePotentiallyResolved",
                table: "cases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StageChangedAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupervisionCycleStartedAt",
                table: "cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "case_emails",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_email_messages_Direction",
                table: "email_messages",
                column: "Direction");

            migrationBuilder.CreateIndex(
                name: "IX_cases_Stage",
                table: "cases",
                column: "Stage");

            // Existing Cases get a stage consistent with their current status, timestamped with their
            // last activity, so supervision does not treat old work as brand new inquiries.
            migrationBuilder.Sql("""
                UPDATE cases SET
                    "StageChangedAt" = "LastActivityAt",
                    "SupervisionCycleStartedAt" = "FirstEmailReceivedAt",
                    "LastCustomerMessageAt" = "LastActivityAt",
                    "FirstAgentResponseAt" = CASE WHEN "ReplyStatus" = 5 THEN "LastActivityAt" ELSE NULL END,
                    "Stage" = CASE
                        WHEN "WorkStatus" = 8 THEN 8
                        WHEN "WorkStatus" IN (9, 10) THEN 9
                        WHEN "WorkStatus" = 3 THEN 4
                        WHEN "WorkStatus" IN (4, 5) THEN 5
                        WHEN "ReplyStatus" = 5 THEN 1
                        ELSE 0 END;
                UPDATE case_emails SET "Role" = 0;
                UPDATE email_messages SET "Direction" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_email_messages_Direction",
                table: "email_messages");

            migrationBuilder.DropIndex(
                name: "IX_cases_Stage",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "BodyTextEncrypted",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "ConversationSyncedThrough",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "EpisodeDeadlineMissedAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "EpisodeEscalations",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "EpisodeLastEscalatedAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "EpisodeLastReminderAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "EpisodeRemindersSent",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "EpisodeWaitNoticeAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "FirstAgentResponseAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "LastAgentMessageAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "LastCustomerMessageAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "LastInternalActivityAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "OverdueEscalatedAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "OverdueSince",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ResolutionAssessedAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ResolutionAssessedMessageCount",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ResolutionConfidence",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ResolutionDueAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ResolutionReason",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ResolutionVerdict",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "StageBeforePotentiallyResolved",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "StageChangedAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "SupervisionCycleStartedAt",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "case_emails");
        }
    }
}
