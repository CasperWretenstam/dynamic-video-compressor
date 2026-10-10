using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dvc.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourceDerivedResolutionFpsAndPreset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Resolution",
                table: "Jobs",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<double>(
                name: "Fps",
                table: "Jobs",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            // Existing rows were encoded without an explicit preset, which is
            // libx264's own "medium".
            migrationBuilder.AddColumn<string>(
                name: "Preset",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "medium");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Preset",
                table: "Jobs");

            migrationBuilder.AlterColumn<string>(
                name: "Resolution",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "720p",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Fps",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);
        }
    }
}
