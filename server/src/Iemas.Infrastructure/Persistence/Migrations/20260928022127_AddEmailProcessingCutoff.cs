using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailProcessingCutoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessEmailsReceivedAfter",
                table: "email_accounts",
                type: "timestamp with time zone",
                nullable: true);
        
            // Mailbox history already stored when this feature arrived is not new work: existing
            // inbound accounts start processing from now, and their waiting old email becomes Historical.
            migrationBuilder.Sql("""
                UPDATE email_accounts SET "ProcessEmailsReceivedAfter" = now() WHERE "Purpose" = 0;
                UPDATE email_messages m SET "ProcessingStatus" = 4
                FROM email_accounts a
                WHERE a."Id" = m."EmailAccountId" AND m."ProcessingStatus" = 0 AND m."ReceivedAt" < a."ProcessEmailsReceivedAfter";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessEmailsReceivedAfter",
                table: "email_accounts");
        }
    }
}
