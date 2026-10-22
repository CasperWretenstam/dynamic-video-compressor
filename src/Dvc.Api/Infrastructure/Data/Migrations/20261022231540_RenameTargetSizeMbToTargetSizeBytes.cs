using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dvc.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTargetSizeMbToTargetSizeBytes : Migration
    {
        // The old column held MiB (1 MB = 1,048,576 bytes in the size math).
        private const long BytesPerMib = 1024 * 1024;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TargetSizeBytes",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            // Rounded down, so a converted target never allows more than the
            // size originally asked for.
            migrationBuilder.Sql(
                $"UPDATE Jobs SET TargetSizeBytes = CAST(TargetSizeMb * {BytesPerMib} AS INTEGER) " +
                "WHERE TargetSizeMb IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "TargetSizeMb",
                table: "Jobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TargetSizeMb",
                table: "Jobs",
                type: "REAL",
                nullable: true);

            migrationBuilder.Sql(
                $"UPDATE Jobs SET TargetSizeMb = TargetSizeBytes / {BytesPerMib}.0 " +
                "WHERE TargetSizeBytes IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "TargetSizeBytes",
                table: "Jobs");
        }
    }
}
