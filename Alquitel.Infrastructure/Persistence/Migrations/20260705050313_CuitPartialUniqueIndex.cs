using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alquitel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CuitPartialUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clients_Cuit",
                table: "Clients");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Cuit",
                table: "Clients",
                column: "Cuit",
                unique: true,
                filter: "\"Cuit\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clients_Cuit",
                table: "Clients");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Cuit",
                table: "Clients",
                column: "Cuit",
                unique: true);
        }
    }
}
