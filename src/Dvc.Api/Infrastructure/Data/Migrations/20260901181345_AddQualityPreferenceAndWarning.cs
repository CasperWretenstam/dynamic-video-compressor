using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dvc.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityPreferenceAndWarning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualityPreference",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "Balanced");

            migrationBuilder.AddColumn<string>(
                name: "Warning",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QualityPreference",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "Warning",
                table: "Jobs");
        }
    }
}
