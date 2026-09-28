using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillEmailReceivedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every email already linked to a Case gets its "Email Received" timeline step (§66),
            // stamped with when it arrived. Idempotent: skipped where one already exists.
            migrationBuilder.Sql("""
                INSERT INTO case_events ("Id", "CaseId", "EventType", "Detail", "ActorEmployeeId", "OccurredAt", "CreatedAt")
                SELECT gen_random_uuid(), ce."CaseId", 0,
                       'Email received from ' || m."FromAddress" || ': "' || m."Subject" || '".',
                       NULL, m."ReceivedAt", now()
                FROM case_emails ce
                JOIN email_messages m ON m."Id" = ce."EmailMessageId"
                WHERE NOT EXISTS (
                    SELECT 1 FROM case_events e
                    WHERE e."CaseId" = ce."CaseId" AND e."EventType" = 0 AND e."OccurredAt" = m."ReceivedAt");
                """);


        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
