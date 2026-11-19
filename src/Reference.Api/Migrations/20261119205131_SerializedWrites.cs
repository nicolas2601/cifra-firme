using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reference.Api.Migrations
{
    /// <inheritdoc />
    public partial class SerializedWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Gate",
                table: "Clock",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Clock",
                keyColumn: "Id",
                keyValue: 1,
                column: "Gate",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Gate",
                table: "Clock");
        }
    }
}
