using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OBC.Evaluation.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelAgentProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentCli",
                table: "Models",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentConfigJson",
                table: "Models",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentCli",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "AgentConfigJson",
                table: "Models");
        }
    }
}
