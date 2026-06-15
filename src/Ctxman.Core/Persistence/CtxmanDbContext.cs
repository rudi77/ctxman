using Ctxman.Core.Domain;
using Ctxman.Core.Persistence.Converters;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Core.Persistence;

/// <summary>
/// EF-Core-DbContext für ctxman (Spec §7, §8). Bildet die fünf Tabellen <c>sessions</c>,
/// <c>segments</c>, <c>frames</c>, <c>events</c> (Outbox, append-only) und
/// <c>idempotency_keys</c> ab — alle mit snake_case-Spalten und einer <c>tenant_id</c>-Spalte.
///
/// Tenant-Isolation (Spec §10): jede Entität trägt einen Global Query Filter auf
/// <c>tenant_id == <see cref="ITenantContext.TenantId"/></c>. Der Tenant kommt ausschließlich
/// aus dem injizierten <see cref="ITenantContext"/> (Middleware-Auflösung, §4.1), nie aus dem
/// Body. Damit ist jede reguläre Query tenant-gefiltert.
///
/// Provider-neutral (CLAUDE.md Teststrategie): IDs als String, Enums als snake_case-String,
/// Value Objects (PolicyConfig, BlobRef) als JSON-Text — funktioniert auf SQLite (Tests) und
/// Npgsql (Produktion) ohne Postgres-spezifische Spaltentypen.
/// </summary>
public sealed class CtxmanDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public CtxmanDbContext(DbContextOptions<CtxmanDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<Frame> Frames => Set<Frame>();
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<IdempotencyKeyRecord> IdempotencyKeys => Set<IdempotencyKeyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureSession(modelBuilder);
        ConfigureSegment(modelBuilder);
        ConfigureFrame(modelBuilder);
        ConfigureEvent(modelBuilder);
        ConfigureIdempotencyKey(modelBuilder);
    }

    private void ConfigureSession(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.Id);

            e.Property(s => s.Id).HasColumnName("id").HasConversion(CtxmanValueConverters.Ulid);
            e.Property(s => s.TenantId).HasColumnName("tenant_id");
            e.Property(s => s.AgentTemplateId).HasColumnName("agent_template_id");
            e.Property(s => s.Policy).HasColumnName("policy").HasConversion(CtxmanValueConverters.PolicyConfig);
            // Spec §4.4: optimistic concurrency. context_version als EF-Concurrency-Token, damit
            // ein verlorenes Update (zwei Worker lesen dieselbe Version, beide schreiben) nicht
            // still durchgeht, sondern als DbUpdateConcurrencyException auffällt — die Endpunkte
            // übersetzen das in 409. Ohne Token wäre If-Match nur ein check-then-act-Race.
            e.Property(s => s.ContextVersion).HasColumnName("context_version").IsConcurrencyToken();
            e.Property(s => s.StaticEpoch).HasColumnName("static_epoch");
            e.Property(s => s.CurrentTurn).HasColumnName("current_turn");
            e.Property(s => s.Status).HasColumnName("status").HasConversion(CtxmanValueConverters.SessionStatus);
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");

            // Frames der Session als 1:n-Beziehung über das Backing-Field _frames
            // (IReadOnlyList ist nur lesbar). Frame bleibt eigenständige Entität, per
            // SessionId verknüpft (Spec §2.5).
            e.HasMany(s => s.Frames)
                .WithOne()
                .HasForeignKey(f => f.SessionId)
                .HasPrincipalKey(s => s.Id);
            e.Navigation(s => s.Frames)
                .HasField("_frames")
                .UsePropertyAccessMode(PropertyAccessMode.Field);

            // Spec §10: jede Query tenant-gefiltert.
            e.HasQueryFilter(s => s.TenantId == _tenant.TenantId);
        });
    }

    private void ConfigureSegment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Segment>(e =>
        {
            e.ToTable("segments");
            e.HasKey(s => s.Id);

            // Segment hat nur einen vollen positionalen Ctor (I2 wird dort erzwungen) und
            // get-only/private-set Properties. EF materialisiert über genau diesen Ctor —
            // die Parameternamen treffen die Property-/Spaltennamen.
            e.Property(s => s.Id).HasColumnName("id").HasConversion(CtxmanValueConverters.Ulid);
            e.Property(s => s.SessionId).HasColumnName("session_id").HasConversion(CtxmanValueConverters.Ulid);
            e.Property(s => s.TenantId).HasColumnName("tenant_id");
            e.Property(s => s.Region).HasColumnName("region").HasConversion(CtxmanValueConverters.Region);
            e.Property(s => s.Kind).HasColumnName("kind");
            e.Property(s => s.Source).HasColumnName("source");
            e.Property(s => s.Role).HasColumnName("role").HasConversion(CtxmanValueConverters.NullableRole);
            e.Property(s => s.Content).HasColumnName("content");
            e.Property(s => s.BlobRef).HasColumnName("blob_ref").HasConversion(CtxmanValueConverters.NullableBlobRef);
            e.Property(s => s.Refetchable).HasColumnName("refetchable");
            e.Property(s => s.Origin).HasColumnName("origin");
            e.Property(s => s.Summary).HasColumnName("summary");
            e.Property(s => s.ToolCallId).HasColumnName("tool_call_id");
            e.Property(s => s.FrameId).HasColumnName("frame_id").HasConversion(CtxmanValueConverters.NullableUlid);
            e.Property(s => s.Pinned).HasColumnName("pinned");
            e.Property(s => s.CreatedTurn).HasColumnName("created_turn");
            e.Property(s => s.LastReferencedTurn).HasColumnName("last_referenced_turn");
            e.Property(s => s.Tokens).HasColumnName("tokens");
            e.Property(s => s.Seq).HasColumnName("seq");
            e.Property(s => s.State).HasColumnName("state").HasConversion(CtxmanValueConverters.SegmentState);

            // Spec §7: segments ist append-heavy; jede Query filtert auf session_id (+ tenant_id über
            // den Query Filter) und der Hot Path berechnet MAX(seq). Ohne Index ⇒ Seq-Scans.
            // (session_id, seq) deckt MAX(seq) und die seq-sortierte Render-Reihenfolge ab; NICHT
            // unique, weil ein compaction_summary die seq seines ältesten (soft-deleted) Quell-
            // Segments übernimmt (Spec §3.3) — seq ist also pro Session nicht kollisionsfrei.
            e.HasIndex(s => new { s.SessionId, s.Seq });
            e.HasIndex(s => s.TenantId);

            // Spec §10: jede Query tenant-gefiltert.
            e.HasQueryFilter(s => s.TenantId == _tenant.TenantId);
        });
    }

    private void ConfigureFrame(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Frame>(e =>
        {
            e.ToTable("frames");
            e.HasKey(f => f.Id);

            e.Property(f => f.Id).HasColumnName("id").HasConversion(CtxmanValueConverters.Ulid);
            e.Property(f => f.SessionId).HasColumnName("session_id").HasConversion(CtxmanValueConverters.Ulid);
            e.Property(f => f.TenantId).HasColumnName("tenant_id");
            e.Property(f => f.ParentFrameId).HasColumnName("parent_frame_id").HasConversion(CtxmanValueConverters.NullableUlid);
            e.Property(f => f.Label).HasColumnName("label");
            e.Property(f => f.OpenedTurn).HasColumnName("opened_turn");
            e.Property(f => f.Status).HasColumnName("status").HasConversion(CtxmanValueConverters.FrameStatus);

            // Spec §10: jede Query tenant-gefiltert.
            e.HasQueryFilter(f => f.TenantId == _tenant.TenantId);
        });
    }

    private void ConfigureEvent(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>(e =>
        {
            e.ToTable("events");
            e.HasKey(ev => ev.Id);

            e.Property(ev => ev.Id).HasColumnName("id").HasConversion(CtxmanValueConverters.Ulid);
            e.Property(ev => ev.TenantId).HasColumnName("tenant_id");
            e.Property(ev => ev.SessionId).HasColumnName("session_id").HasConversion(CtxmanValueConverters.NullableUlid);
            e.Property(ev => ev.Type).HasColumnName("type");
            e.Property(ev => ev.Payload).HasColumnName("payload");
            e.Property(ev => ev.Seq).HasColumnName("seq");
            e.Property(ev => ev.CreatedAt).HasColumnName("created_at");

            // Spec §6: der Event-Feed liest (session_id, seq > after_seq) aufsteigend und der
            // Outbox-Cursor berechnet MAX(seq) pro Session — Index hält beides indexgestützt.
            e.HasIndex(ev => new { ev.SessionId, ev.Seq });

            // Spec §10: jede Query tenant-gefiltert (Outbox ist append-only, §7/§10).
            e.HasQueryFilter(ev => ev.TenantId == _tenant.TenantId);
        });
    }

    private void ConfigureIdempotencyKey(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyKeyRecord>(e =>
        {
            e.ToTable("idempotency_keys");
            // Spec §4.4: Key ist pro Tenant eindeutig — zusammengesetzter Schlüssel (tenant_id, key).
            e.HasKey(k => new { k.TenantId, k.Key });

            e.Property(k => k.TenantId).HasColumnName("tenant_id");
            e.Property(k => k.Key).HasColumnName("key");
            e.Property(k => k.SessionId).HasColumnName("session_id").HasConversion(CtxmanValueConverters.Ulid);
            e.Property(k => k.ResponseStatusCode).HasColumnName("response_status_code");
            e.Property(k => k.ResponseBody).HasColumnName("response_body");
            e.Property(k => k.CreatedAt).HasColumnName("created_at");
            e.Property(k => k.ExpiresAt).HasColumnName("expires_at");

            // Spec §10: jede Query tenant-gefiltert.
            e.HasQueryFilter(k => k.TenantId == _tenant.TenantId);
        });
    }
}
