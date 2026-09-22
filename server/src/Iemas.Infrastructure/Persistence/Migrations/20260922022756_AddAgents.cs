using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EnrollmentEmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServerAddress = table.Column<string>(type: "text", nullable: true),
                    LastKnownClientIp = table.Column<string>(type: "text", nullable: true),
                    AgentVersion = table.Column<string>(type: "text", nullable: true),
                    DeviceMetadata = table.Column<string>(type: "text", nullable: true),
                    RegistrationStatus = table.Column<int>(type: "integer", nullable: false),
                    ConnectionStatus = table.Column<int>(type: "integer", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "text", nullable: true),
                    LastConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RegistrationRequestToken = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agents_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agents_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_case_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    ClientTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ServerTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CaseEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_case_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_case_actions_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_case_actions_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_case_actions_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingKeyCiphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    PendingKeyNonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    PendingKeyTag = table.Column<byte[]>(type: "bytea", nullable: true),
                    PendingKeyEncryptionKeyId = table.Column<string>(type: "text", nullable: true),
                    RawKeyCollected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_credentials_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_logs_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_case_actions_AgentId_RequestId",
                table: "agent_case_actions",
                columns: new[] { "AgentId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_case_actions_CaseId",
                table: "agent_case_actions",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_case_actions_EmployeeId",
                table: "agent_case_actions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_credentials_AgentId",
                table: "agent_credentials",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_credentials_KeyHash",
                table: "agent_credentials",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_logs_AgentId",
                table: "agent_logs",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_logs_OccurredAt",
                table: "agent_logs",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_agents_ApprovedByUserId",
                table: "agents",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agents_EmployeeId",
                table: "agents",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_agents_RegistrationRequestToken",
                table: "agents",
                column: "RegistrationRequestToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_RegistrationStatus",
                table: "agents",
                column: "RegistrationStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_case_actions");

            migrationBuilder.DropTable(
                name: "agent_credentials");

            migrationBuilder.DropTable(
                name: "agent_logs");

            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
