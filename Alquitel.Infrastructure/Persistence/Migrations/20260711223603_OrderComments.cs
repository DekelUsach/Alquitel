using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alquitel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Comments",
                table: "Orders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Comments",
                table: "Orders");
        }
    }
}
