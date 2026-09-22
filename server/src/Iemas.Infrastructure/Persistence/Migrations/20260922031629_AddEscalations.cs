using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEscalations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "escalation_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escalation_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "escalation_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    ClassificationProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Categories = table.Column<string>(type: "text", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: true),
                    TriggerReminderCount = table.Column<int>(type: "integer", nullable: false),
                    GracePeriod = table.Column<TimeSpan>(type: "interval", nullable: false),
                    Cooldown = table.Column<TimeSpan>(type: "interval", nullable: false),
                    MaximumLevel = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escalation_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "escalation_group_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EscalationGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escalation_group_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_escalation_group_members_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_escalation_group_members_escalation_groups_EscalationGroupId",
                        column: x => x.EscalationGroupId,
                        principalTable: "escalation_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "escalation_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    EscalationPolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "text", nullable: false),
                    RecipientType = table.Column<int>(type: "integer", nullable: true),
                    RecipientDisplay = table.Column<string>(type: "text", nullable: true),
                    RecipientEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Channel = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    SkipReason = table.Column<int>(type: "integer", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    EmailMessageId = table.Column<string>(type: "text", nullable: true),
                    DeliveryResult = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escalation_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_escalation_events_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_escalation_events_escalation_policies_EscalationPolicyId",
                        column: x => x.EscalationPolicyId,
                        principalTable: "escalation_policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "escalation_levels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EscalationPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    DelayAfterPreviousLevel = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RecipientType = table.Column<int>(type: "integer", nullable: false),
                    SpecificEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SpecificGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escalation_levels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_escalation_levels_employees_SpecificEmployeeId",
                        column: x => x.SpecificEmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_escalation_levels_escalation_groups_SpecificGroupId",
                        column: x => x.SpecificGroupId,
                        principalTable: "escalation_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_escalation_levels_escalation_policies_EscalationPolicyId",
                        column: x => x.EscalationPolicyId,
                        principalTable: "escalation_policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_escalation_events_CaseId",
                table: "escalation_events",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_events_CaseId_EscalationPolicyId_Level_Outcome",
                table: "escalation_events",
                columns: new[] { "CaseId", "EscalationPolicyId", "Level", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_escalation_events_EscalationPolicyId",
                table: "escalation_events",
                column: "EscalationPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_events_OccurredAt",
                table: "escalation_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_group_members_EmployeeId",
                table: "escalation_group_members",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_group_members_EscalationGroupId_EmployeeId",
                table: "escalation_group_members",
                columns: new[] { "EscalationGroupId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_escalation_groups_Name",
                table: "escalation_groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_escalation_levels_EscalationPolicyId_Level",
                table: "escalation_levels",
                columns: new[] { "EscalationPolicyId", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_escalation_levels_SpecificEmployeeId",
                table: "escalation_levels",
                column: "SpecificEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_levels_SpecificGroupId",
                table: "escalation_levels",
                column: "SpecificGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_policies_ClassificationProfileId",
                table: "escalation_policies",
                column: "ClassificationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_policies_IsDefault",
                table: "escalation_policies",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_escalation_policies_Name",
                table: "escalation_policies",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "escalation_events");

            migrationBuilder.DropTable(
                name: "escalation_group_members");

            migrationBuilder.DropTable(
                name: "escalation_levels");

            migrationBuilder.DropTable(
                name: "escalation_groups");

            migrationBuilder.DropTable(
                name: "escalation_policies");
        }
    }
}
