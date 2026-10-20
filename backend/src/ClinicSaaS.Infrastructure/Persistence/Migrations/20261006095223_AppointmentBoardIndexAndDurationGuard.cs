using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentBoardIndexAndDurationGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Tenant_Start",
                table: "Appointments",
                columns: new[] { "TenantId", "StartUtc" })
                .Annotation("SqlServer:Include", new[] { "Status", "Source" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointments_MaxDuration",
                table: "Appointments",
                sql: "DATEDIFF(minute, [StartUtc], [EndUtc]) <= 480");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Appointments_Tenant_Start",
                table: "Appointments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointments_MaxDuration",
                table: "Appointments");
        }
    }
}
