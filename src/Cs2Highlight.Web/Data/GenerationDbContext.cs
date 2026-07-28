using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Data;

public sealed class GenerationDbContext(DbContextOptions<GenerationDbContext> options)
    : DbContext(options)
{
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<GenerationDemo> GenerationDemos => Set<GenerationDemo>();
    public DbSet<GenerationPlayer> GenerationPlayers => Set<GenerationPlayer>();
    public DbSet<GenerationHighlight> GenerationHighlights => Set<GenerationHighlight>();
    public DbSet<GenerationArtifact> GenerationArtifacts => Set<GenerationArtifact>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<GenerationEvent> GenerationEvents => Set<GenerationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Generation>().HasIndex(value => value.PublicId).IsUnique();
        modelBuilder.Entity<Generation>().Property(value => value.Status).HasConversion<string>();
        modelBuilder.Entity<Generation>().Property(value => value.PaymentStatus).HasConversion<string>();
        modelBuilder.Entity<Generation>().Property(value => value.OutputOrder).HasConversion<string>();
        modelBuilder.Entity<Generation>().Property(value => value.TransitionType).HasConversion<string>();
        modelBuilder.Entity<Generation>().Property(value => value.Version).IsConcurrencyToken();
        modelBuilder.Entity<GenerationDemo>()
            .HasIndex(value => new { value.GenerationId, value.Sha256 }).IsUnique();
        modelBuilder.Entity<GenerationDemo>().Property(value => value.AnalysisStatus).HasConversion<string>();
        modelBuilder.Entity<GenerationPlayer>()
            .HasIndex(value => new { value.GenerationId, value.SteamId }).IsUnique();
        modelBuilder.Entity<GenerationHighlight>()
            .HasIndex(value => new { value.GenerationId, value.GenerationDemoId, value.HighlightId }).IsUnique();
        modelBuilder.Entity<GenerationArtifact>().Property(value => value.Type).HasConversion<string>();
        modelBuilder.Entity<Payment>().Property(value => value.Status).HasConversion<string>();
        modelBuilder.Entity<Payment>().HasIndex(value => value.IdempotencyKey).IsUnique();
        modelBuilder.Entity<Payment>().HasIndex(value => value.ProviderPaymentId).IsUnique();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Generation>().Where(entry =>
                     entry.State == EntityState.Modified))
        {
            entry.Entity.Version++;
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
