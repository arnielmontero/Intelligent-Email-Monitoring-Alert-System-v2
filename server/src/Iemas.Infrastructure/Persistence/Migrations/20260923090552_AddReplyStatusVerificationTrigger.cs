using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 10 data integrity — enforces the §43/§46 claim-vs-verified-fact invariant
    /// (AgentCaseActionService.cs's class doc: "ALREADY_REPLIED... never touches Case.ReplyStatus...
    /// only Phase 6's ReplyVerificationService... may set [it]") at the database level, not just by
    /// application-code discipline. Previously this was true only because no line in
    /// AgentCaseActionService.cs happens to write to ReplyStatus — a correct but structurally
    /// unenforced state of affairs that a future code change could silently violate.
    ///
    /// The trigger rejects any INSERT/UPDATE that would set cases."ReplyStatus" = 5 (Replied)
    /// unless a corresponding reply_verification_attempts row with "Outcome" = 0 (VerifiedReply)
    /// already exists for that case. This directly encodes "a verified fact needs verification
    /// evidence" — it cannot be bypassed by any future code path, EF Core or otherwise, that writes
    /// to the cases table.
    /// </summary>
    public partial class AddReplyStatusVerificationTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION enforce_reply_status_requires_verification()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW."ReplyStatus" = 5 AND NOT EXISTS (
                        SELECT 1 FROM reply_verification_attempts
                        WHERE "CaseId" = NEW."Id" AND "Outcome" = 0
                    ) THEN
                        RAISE EXCEPTION
                            'ReplyStatus cannot be set to Replied (5) for case % without a corresponding ReplyVerificationAttempt row with Outcome = VerifiedReply (0). This is a claim-vs-verified-fact violation (see AgentCaseActionService class doc, Requirements §43/§46).', NEW."Id"
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_enforce_reply_status_requires_verification
                BEFORE INSERT OR UPDATE OF "ReplyStatus" ON cases
                FOR EACH ROW EXECUTE FUNCTION enforce_reply_status_requires_verification();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_enforce_reply_status_requires_verification ON cases;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS enforce_reply_status_requires_verification();");
        }
    }
}
