using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dvc.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameJobPresetToEncoderPreset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Preset",
                table: "Jobs",
                newName: "EncoderPreset");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EncoderPreset",
                table: "Jobs",
                newName: "Preset");
        }
    }
}
