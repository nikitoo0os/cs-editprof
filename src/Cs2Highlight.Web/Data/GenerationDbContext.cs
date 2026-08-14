using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Data;

public sealed class GenerationDbContext(DbContextOptions<GenerationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ApplicationUser> ApplicationUsers => Set<ApplicationUser>();
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<GenerationDemo> GenerationDemos => Set<GenerationDemo>();
    public DbSet<GenerationPlayer> GenerationPlayers => Set<GenerationPlayer>();
    public DbSet<GenerationHighlight> GenerationHighlights => Set<GenerationHighlight>();
    public DbSet<GenerationArtifact> GenerationArtifacts => Set<GenerationArtifact>();
    public DbSet<GenerationEffectPlan> GenerationEffectPlans => Set<GenerationEffectPlan>();
    public DbSet<GenerationMusic> GenerationMusic => Set<GenerationMusic>();
    public DbSet<GenerationMovieSettings> GenerationMovieSettings => Set<GenerationMovieSettings>();
    public DbSet<GenerationMusicAnchor> GenerationMusicAnchors => Set<GenerationMusicAnchor>();
    public DbSet<GenerationEditSegment> GenerationEditSegments => Set<GenerationEditSegment>();
    public DbSet<GenerationMusicSection> GenerationMusicSections => Set<GenerationMusicSection>();
    public DbSet<GenerationBrollCandidate> GenerationBrollCandidates => Set<GenerationBrollCandidate>();
    public DbSet<GenerationCameraShot> GenerationCameraShots => Set<GenerationCameraShot>();
    public DbSet<GenerationCinematicPlan> GenerationCinematicPlans => Set<GenerationCinematicPlan>();
    public DbSet<GenerationTimelinePlan> GenerationTimelinePlans => Set<GenerationTimelinePlan>();
    public DbSet<GenerationTimelineAnchor> GenerationTimelineAnchors => Set<GenerationTimelineAnchor>();
    public DbSet<GenerationTimelineGap> GenerationTimelineGaps => Set<GenerationTimelineGap>();
    public DbSet<GenerationTimelineRevision> GenerationTimelineRevisions => Set<GenerationTimelineRevision>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<GenerationEvent> GenerationEvents => Set<GenerationEvent>();
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();
    public DbSet<TokenTransaction> TokenTransactions => Set<TokenTransaction>();
    public DbSet<TokenPackage> TokenPackages => Set<TokenPackage>();
    public DbSet<TokenPurchase> TokenPurchases => Set<TokenPurchase>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<SteamHistoryConnection> SteamHistoryConnections => Set<SteamHistoryConnection>();
    public DbSet<SteamHistoryMatch> SteamHistoryMatches => Set<SteamHistoryMatch>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ApplicationUser>().HasIndex(value => value.ReferralCode).IsUnique();
        builder.Entity<ApplicationUser>().Property(value => value.ThemePreference).HasDefaultValue("redline");
        builder.Entity<UserConsent>().Property(value => value.ConsentType).HasConversion<string>();
        builder.Entity<UserConsent>().HasIndex(value => new { value.UserId, value.ConsentType, value.DocumentVersion }).IsUnique();
        builder.Entity<TokenTransaction>().Property(value => value.Type).HasConversion<string>();
        builder.Entity<TokenTransaction>().Property(value => value.Status).HasConversion<string>();
        builder.Entity<TokenTransaction>().HasIndex(value => new { value.UserId, value.IdempotencyKey }).IsUnique();
        builder.Entity<TokenPackage>().HasIndex(value => value.Code).IsUnique();
        builder.Entity<TokenPurchase>().Property(value => value.Status).HasConversion<string>();
        builder.Entity<TokenPurchase>().HasIndex(value => value.IdempotencyKey).IsUnique();
        builder.Entity<TokenPurchase>().HasIndex(value => value.ProviderPaymentId).IsUnique();
        builder.Entity<Referral>().HasIndex(value => value.ReferredUserId).IsUnique();
        builder.Entity<Referral>().HasIndex(value => new { value.ReferrerUserId, value.ReferredUserId }).IsUnique();
        builder.Entity<SteamHistoryConnection>().HasIndex(value => value.UserId).IsUnique();
        builder.Entity<SteamHistoryConnection>().HasOne(value => value.User)
            .WithOne(value => value.SteamHistoryConnection)
            .HasForeignKey<SteamHistoryConnection>(value => value.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SteamHistoryMatch>().Property(value => value.Availability).HasConversion<string>();
        builder.Entity<SteamHistoryMatch>()
            .HasIndex(value => new { value.SteamHistoryConnectionId, value.ShareCode }).IsUnique();
        builder.Entity<SteamHistoryMatch>().HasOne(value => value.Connection)
            .WithMany(value => value.Matches)
            .HasForeignKey(value => value.SteamHistoryConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Generation>().HasIndex(value => value.PublicId).IsUnique();
        builder.Entity<Generation>().Property(value => value.Status).HasConversion<string>();
        builder.Entity<Generation>().Property(value => value.PaymentStatus).HasConversion<string>();
        builder.Entity<Generation>().Property(value => value.OutputOrder).HasConversion<string>();
        builder.Entity<Generation>().Property(value => value.TransitionType).HasConversion<string>();
        builder.Entity<Generation>().Property(value => value.EffectPreset).HasConversion<string>();
        builder.Entity<Generation>().Property(value => value.Version).IsConcurrencyToken();
        builder.Entity<GenerationDemo>()
            .HasIndex(value => new { value.GenerationId, value.Sha256 }).IsUnique();
        builder.Entity<GenerationDemo>().Property(value => value.AnalysisStatus).HasConversion<string>();
        builder.Entity<GenerationPlayer>()
            .HasIndex(value => new { value.GenerationId, value.SteamId }).IsUnique();
        builder.Entity<GenerationHighlight>()
            .HasIndex(value => new { value.GenerationId, value.GenerationDemoId, value.HighlightId }).IsUnique();
        builder.Entity<GenerationEffectPlan>().Property(value => value.Preset).HasConversion<string>();
        builder.Entity<GenerationEffectPlan>()
            .HasIndex(value => new { value.GenerationId, value.GenerationHighlightId }).IsUnique();
        builder.Entity<GenerationMusic>()
            .HasIndex(value => value.GenerationId).IsUnique();
        builder.Entity<GenerationMovieSettings>()
            .HasIndex(value => value.GenerationId).IsUnique();
        builder.Entity<GenerationMovieSettings>().Property(value => value.MovieStyle).HasConversion<string>();
        builder.Entity<GenerationMovieSettings>().Property(value => value.EffectIntensity).HasConversion<string>();
        builder.Entity<GenerationMovieSettings>().Property(value => value.SyncIntensity).HasConversion<string>();
        builder.Entity<GenerationMovieSettings>().Property(value => value.ColorGradePreset).HasConversion<string>();
        builder.Entity<GenerationMovieSettings>().Property(value => value.MusicDurationPolicy).HasConversion<string>();
        builder.Entity<GenerationMovieSettings>().Property(value => value.CinematicDuration).HasConversion<string>();
        builder.Entity<GenerationMovieSettings>().Property(value => value.CinematicEditIntensity).HasConversion<string>();
        builder.Entity<GenerationMusicAnchor>().Property(value => value.Type).HasConversion<string>();
        builder.Entity<GenerationMusicAnchor>()
            .HasIndex(value => new { value.GenerationId, value.AnchorId }).IsUnique();
        builder.Entity<GenerationEditSegment>()
            .HasIndex(value => new { value.GenerationId, value.Sequence }).IsUnique();
        builder.Entity<GenerationMusicSection>().Property(value => value.Type).HasConversion<string>();
        builder.Entity<GenerationMusicSection>()
            .HasIndex(value => new { value.GenerationId, value.SectionId }).IsUnique();
        builder.Entity<GenerationBrollCandidate>().Property(value => value.Type).HasConversion<string>();
        builder.Entity<GenerationBrollCandidate>()
            .HasIndex(value => new { value.GenerationId, value.CandidateId }).IsUnique();
        builder.Entity<GenerationCameraShot>().Property(value => value.Type).HasConversion<string>();
        builder.Entity<GenerationCameraShot>().Property(value => value.PreviewStatus).HasConversion<string>();
        builder.Entity<GenerationCameraShot>().Property(value => value.FallbackType).HasConversion<string>();
        builder.Entity<GenerationCameraShot>()
            .HasIndex(value => new { value.GenerationId, value.ShotId }).IsUnique();
        builder.Entity<GenerationCinematicPlan>()
            .HasIndex(value => value.GenerationId).IsUnique();
        builder.Entity<GenerationTimelinePlan>()
            .HasIndex(value => value.GenerationId).IsUnique();
        builder.Entity<GenerationTimelinePlan>()
            .Property(value => value.Mode).HasConversion<string>();
        builder.Entity<GenerationTimelinePlan>()
            .Property(value => value.State).HasConversion<string>();
        builder.Entity<GenerationTimelineAnchor>()
            .HasIndex(value => new { value.TimelinePlanId, value.AnchorId })
            .IsUnique();
        builder.Entity<GenerationTimelineAnchor>()
            .Property(value => value.MarkerType).HasConversion<string>();
        builder.Entity<GenerationTimelineAnchor>()
            .Property(value => value.FeasibilityStatus).HasConversion<string>();
        builder.Entity<GenerationTimelineGap>()
            .HasIndex(value => new { value.TimelinePlanId, value.GapId })
            .IsUnique();
        builder.Entity<GenerationTimelineGap>()
            .Property(value => value.Role).HasConversion<string>();
        builder.Entity<GenerationTimelineGap>()
            .Property(value => value.State).HasConversion<string>();
        builder.Entity<GenerationTimelineRevision>()
            .HasIndex(value => new { value.TimelinePlanId, value.Number })
            .IsUnique();
        builder.Entity<GenerationArtifact>().Property(value => value.Type).HasConversion<string>();
        builder.Entity<Payment>().Property(value => value.Status).HasConversion<string>();
        builder.Entity<Payment>().HasIndex(value => value.IdempotencyKey).IsUnique();
        builder.Entity<Payment>().HasIndex(value => value.ProviderPaymentId).IsUnique();
        builder.Entity<Generation>().Property(value => value.CleanupStatus).HasConversion<string>();
        builder.Entity<Generation>().HasIndex(value => new { value.UserId, value.CreatedAt });
        builder.Entity<Generation>().HasOne(value => value.User)
            .WithMany(value => value.Generations)
            .HasForeignKey(value => value.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.Entity<TokenTransaction>().HasOne(value => value.Generation)
            .WithMany().HasForeignKey(value => value.GenerationId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<TokenTransaction>().HasOne(value => value.TokenPurchase)
            .WithMany().HasForeignKey(value => value.TokenPurchaseId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<TokenTransaction>().HasOne(value => value.Referral)
            .WithMany().HasForeignKey(value => value.ReferralId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<Referral>().HasOne(value => value.ReferrerUser)
            .WithMany().HasForeignKey(value => value.ReferrerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Referral>().HasOne(value => value.ReferredUser)
            .WithMany().HasForeignKey(value => value.ReferredUserId).OnDelete(DeleteBehavior.Restrict);
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
