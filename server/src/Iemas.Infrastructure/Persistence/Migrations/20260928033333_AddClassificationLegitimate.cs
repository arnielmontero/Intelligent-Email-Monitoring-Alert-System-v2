using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClassificationLegitimate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Legitimate",
                table: "email_classifications",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Legitimate",
                table: "email_classifications");
        }
    }
}
