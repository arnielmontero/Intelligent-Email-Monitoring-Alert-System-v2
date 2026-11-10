using Iemas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Rule-list settings (inquiry phrases, not-an-inquiry phrases, product terms ...) are allowed up to 9,000
    /// characters by the settings catalog, but the column held only 4,000, so saving a long list failed. The hidden
    /// AI model benchmark results row (JSON) needs room too.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261110003800_WidenSystemSettingValueTo20000")]
    public partial class WidenSystemSettingValueTo20000 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "system_settings",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "system_settings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20000)",
                oldMaxLength: 20000);
        }
    }
}
