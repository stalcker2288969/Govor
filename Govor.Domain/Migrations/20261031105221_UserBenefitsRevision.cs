using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Govor.Domain.Migrations
{
    /// <inheritdoc />
    public partial class UserBenefitsRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "UserBenefits",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "UserBenefits");
        }
    }
}
