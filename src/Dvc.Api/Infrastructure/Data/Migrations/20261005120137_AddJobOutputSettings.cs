using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dvc.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobOutputSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "OutputFps",
                table: "Jobs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputHeight",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputWidth",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutputFps",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "OutputHeight",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "OutputWidth",
                table: "Jobs");
        }
    }
}
