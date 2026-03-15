using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DartGameAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddCalibrationMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Calibrations_CameraId",
                table: "Calibrations");

            migrationBuilder.AddColumn<string>(
                name: "CalibrationMethod",
                table: "Calibrations",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Calibrations_CameraId_CalibrationMethod",
                table: "Calibrations",
                columns: new[] { "CameraId", "CalibrationMethod" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Calibrations_CameraId_CalibrationMethod",
                table: "Calibrations");

            migrationBuilder.DropColumn(
                name: "CalibrationMethod",
                table: "Calibrations");

            migrationBuilder.CreateIndex(
                name: "IX_Calibrations_CameraId",
                table: "Calibrations",
                column: "CameraId",
                unique: true);
        }
    }
}
