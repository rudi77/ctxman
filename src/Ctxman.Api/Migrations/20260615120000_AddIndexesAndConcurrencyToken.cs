using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ctxman.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexesAndConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec §7: Indizes auf den append-heavy Hot-Tabellen — jede Query filtert session_id
            // (+ tenant_id über den Query Filter) und der Hot Path berechnet MAX(seq).
            migrationBuilder.CreateIndex(
                name: "IX_segments_session_id_seq",
                table: "segments",
                columns: new[] { "session_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "IX_segments_tenant_id",
                table: "segments",
                column: "tenant_id");

            // Spec §6: Event-Feed liest (session_id, seq > after_seq); Cursor berechnet MAX(seq).
            migrationBuilder.CreateIndex(
                name: "IX_events_session_id_seq",
                table: "events",
                columns: new[] { "session_id", "seq" });

            // Hinweis: context_version als Concurrency-Token (Spec §4.4) ist eine reine
            // EF-Verhaltensänderung (WHERE context_version = @original beim UPDATE) und erfordert
            // kein DDL — die Spalte existiert bereits aus der Initial-Migration.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_segments_session_id_seq",
                table: "segments");

            migrationBuilder.DropIndex(
                name: "IX_segments_tenant_id",
                table: "segments");

            migrationBuilder.DropIndex(
                name: "IX_events_session_id_seq",
                table: "events");
        }
    }
}
