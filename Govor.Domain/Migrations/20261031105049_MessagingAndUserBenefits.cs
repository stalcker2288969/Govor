using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Govor.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MessagingAndUserBenefits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ForwardedFromMessageId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ForwardedFromSentAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ForwardedFromUserId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserBenefits",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Privileges = table.Column<long>(type: "bigint", nullable: false),
                    TagJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserBenefits", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserBenefits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserBenefits");

            migrationBuilder.DropColumn(
                name: "ForwardedFromMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ForwardedFromSentAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ForwardedFromUserId",
                table: "Messages");
        }
    }
}
