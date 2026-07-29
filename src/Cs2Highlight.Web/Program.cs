using System.Threading.RateLimiting;
using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Hubs;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    "appsettings.local.json", optional: true, reloadOnChange: true);
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.AddSignalR();
builder.Services.AddDbContextFactory<GenerationDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("GenerationDb") ??
            "Data Source=storage/generations.db",
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
UploadOptions uploadOptions = builder.Configuration.GetSection("Uploads").Get<UploadOptions>() ?? new();
StorageOptions storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
PipelineOptions pipelineOptions = builder.Configuration.GetSection("Pipeline").Get<PipelineOptions>() ?? new();
RetentionOptions retentionOptions = builder.Configuration.GetSection("Retention").Get<RetentionOptions>() ?? new();
MusicUploadOptions musicUploadOptions =
    builder.Configuration.GetSection("MusicUploads").Get<MusicUploadOptions>() ?? new();
TrustedLutOptions trustedLutOptions =
    builder.Configuration.GetSection("TrustedLuts").Get<TrustedLutOptions>() ?? new();
RecommendedSelectionOptions selectionOptions =
    builder.Configuration.GetSection("RecommendedSelection").Get<RecommendedSelectionOptions>() ?? new();
builder.Services.AddSingleton(uploadOptions);
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(pipelineOptions);
builder.Services.AddSingleton(retentionOptions);
builder.Services.AddSingleton(musicUploadOptions);
builder.Services.AddSingleton(trustedLutOptions);
builder.Services.AddSingleton(selectionOptions);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadOptions.MaximumTotalSizeBytes;
    options.MemoryBufferThreshold = 64 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = uploadOptions.MaximumTotalSizeBytes);
builder.Services.AddSingleton<GenerationStorage>();
builder.Services.AddSingleton<DemoUploadService>();
builder.Services.AddSingleton<IMusicMediaValidator, FfprobeMusicMediaValidator>();
builder.Services.AddSingleton<MusicUploadService>();
builder.Services.AddSingleton<TrustedLutCatalog>();
builder.Services.AddSingleton<IMusicAnalyzerClient, ProcessMusicAnalyzerClient>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMusicalAnchorBuilder, Cs2Highlight.Music.MusicalAnchorBuilder>();
builder.Services.AddSingleton<Cs2Highlight.Music.IHighlightImportanceCalculator, Cs2Highlight.Music.HighlightImportanceCalculator>();
builder.Services.AddSingleton<Cs2Highlight.Music.ITimeWarpPlanner, Cs2Highlight.Music.TimeWarpPlanner>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMusicEditPlanner, Cs2Highlight.Music.MusicEditPlanner>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMusicSectionClassifier, Cs2Highlight.Music.MusicSectionClassifier>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMusicalPeakDetector, Cs2Highlight.Music.MusicalPeakDetector>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMusicNarrativeAnalyzer, Cs2Highlight.Music.MusicNarrativeAnalyzer>();
builder.Services.AddSingleton<Cs2Highlight.Music.ICinematicDurationPolicy, Cs2Highlight.Music.CinematicDurationPolicy>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMusicExcerptSelector, Cs2Highlight.Music.MusicExcerptSelector>();
builder.Services.AddSingleton<Cs2Highlight.Music.IBrollCandidateDetector, Cs2Highlight.Music.BrollCandidateDetector>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMapCameraProfileCatalog, Cs2Highlight.Music.MapCameraProfileCatalog>();
builder.Services.AddSingleton<Cs2Highlight.Music.ICameraPathPlanner, Cs2Highlight.Music.CameraPathPlanner>();
builder.Services.AddSingleton<Cs2Highlight.Music.ICameraShotQualityAnalyzer, Cs2Highlight.Music.CameraShotQualityAnalyzer>();
builder.Services.AddSingleton<Cs2Highlight.Music.IHighlightPeakMatcher, Cs2Highlight.Music.HighlightPeakMatcher>();
builder.Services.AddSingleton<Cs2Highlight.Music.ICinematicTimeWarpPolicy, Cs2Highlight.Music.CinematicTimeWarpPolicy>();
builder.Services.AddSingleton<Cs2Highlight.Music.IMotivatedEffectPlanner, Cs2Highlight.Music.MotivatedEffectPlanner>();
builder.Services.AddSingleton<Cs2Highlight.Music.ISoundDesignPlanner, Cs2Highlight.Music.SoundDesignPlanner>();
builder.Services.AddSingleton<Cs2Highlight.Music.IColorNarrativePlanner, Cs2Highlight.Music.ColorNarrativePlanner>();
builder.Services.AddSingleton<Cs2Highlight.Music.ICinematicDirector, Cs2Highlight.Music.CinematicDirector>();
builder.Services.AddSingleton<Cs2Highlight.Music.ICinematicMusicEditPlanAdapter, Cs2Highlight.Music.CinematicMusicEditPlanAdapter>();
builder.Services.AddSingleton<ICinematicPlanService, CinematicPlanService>();
builder.Services.AddSingleton<GenerationWakeSignal>();
builder.Services.AddSingleton<GenerationCancellationRegistry>();
builder.Services.AddSingleton<GlobalHighlightSelector>();
builder.Services.AddSingleton<Cs2Highlight.Analysis.IWeaponCatalog, Cs2Highlight.Analysis.WeaponCatalog>();
builder.Services.AddScoped<HighlightSelectionService>();
builder.Services.AddSingleton<IEffectPlanner, EffectPlanner>();
builder.Services.AddSingleton<IEffectFilterGraphBuilder, FfmpegEffectFilterGraphBuilder>();
builder.Services.AddSingleton<IEffectSeedProvider, Sha256EffectSeedProvider>();
builder.Services.AddSingleton<IEffectCompatibilityPolicy, EffectCompatibilityPolicy>();
builder.Services.AddSingleton<IEffectBudgetPolicy, EffectBudgetPolicy>();
builder.Services.AddSingleton<IEffectVarietyPolicy, EffectVarietyPolicy>();
builder.Services.AddSingleton<IEffectTimeMapper, EffectTimeMapper>();
builder.Services.AddSingleton<IDynamicEffectPlanner, DynamicEffectPlanner>();
builder.Services.AddSingleton<ICinematicDynamicEffectAdapter, CinematicDynamicEffectAdapter>();
builder.Services.AddSingleton<IFfmpegCapabilityScanner, FfmpegCapabilityScanner>();
builder.Services.AddSingleton<IDynamicEffectFilterGraphBuilder, DynamicEffectFilterGraphBuilder>();
builder.Services.AddSingleton<IHighlightCompilationService, FfmpegHighlightCompilationService>();
builder.Services.AddSingleton<IPaymentProvider, TestPaymentProvider>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<PaymentService>();
builder.Services.AddHostedService<GenerationWorker>();
builder.Services.AddHostedService<GenerationCleanupService>();
builder.Services.AddHealthChecks().AddCheck<GenerationReadinessHealthCheck>("pipeline");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("uploads", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

WebApplication app = builder.Build();
Directory.CreateDirectory(Path.GetDirectoryName(
    Path.GetFullPath(builder.Configuration.GetConnectionString("GenerationDb")?
        .Replace("Data Source=", string.Empty, StringComparison.OrdinalIgnoreCase) ??
        "storage/generations.db"))!);
await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    IDbContextFactory<GenerationDbContext> factory =
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<GenerationDbContext>>();
    await using GenerationDbContext db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self'; connect-src 'self' ws: wss:";
    await next();
});
if (app.Configuration.GetValue("HttpsRedirection:Enabled", false))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.MapRazorPages();
app.MapHub<GenerationHub>("/hubs/generations");
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/api/generations/{publicId}", async (
    string publicId,
    IDbContextFactory<GenerationDbContext> factory,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    Generation? generation = await db.Generations.AsNoTracking()
        .SingleOrDefaultAsync(
            value => value.PublicId == publicId,
            cancellationToken);
    if (generation is null) return Results.NotFound();
    int demoCount = await db.GenerationDemos.CountAsync(
        value => value.GenerationId == generation.Id,
        cancellationToken);
    int playerCount = await db.GenerationPlayers.CountAsync(
        value => value.GenerationId == generation.Id,
        cancellationToken);
    int highlightCount = await db.GenerationHighlights.CountAsync(
        value => value.GenerationId == generation.Id,
        cancellationToken);
    var events = await db.GenerationEvents.AsNoTracking()
        .Where(value => value.GenerationId == generation.Id)
        .OrderByDescending(value => value.Id)
        .Take(8)
        .OrderBy(value => value.Id)
        .Select(value => new
        {
            value.Id,
            value.Stage,
            value.Message,
            value.ProgressPercent,
            value.CreatedAt
        })
        .ToArrayAsync(cancellationToken);
    bool completed = generation.Status is
        GenerationStatus.Completed or GenerationStatus.CompletedWithWarnings;
    string? actionUrl = generation.Status switch
    {
        GenerationStatus.AwaitingPlayerSelection =>
            $"/generations/{publicId}/player",
        GenerationStatus.AwaitingHighlightSelection =>
            $"/generations/{publicId}/highlights",
        GenerationStatus.AwaitingMusicUpload or
        GenerationStatus.AwaitingMovieConfiguration =>
            $"/generations/{publicId}/music",
        _ => null
    };
    return Results.Ok(new
    {
        publicId,
        status = generation.Status.ToString(),
        stage = generation.CurrentStage,
        generation.ProgressPercent,
        demoCount,
        playerCount,
        highlightCount,
        generation.ErrorCode,
        generation.ErrorMessage,
        completed,
        actionUrl,
        videoUrl = completed ? $"/generations/{publicId}/video" : null,
        events
    });
});
app.MapGet("/api/generations/{publicId}/highlights", async (
    string publicId,
    IDbContextFactory<GenerationDbContext> factory,
    IWeaponCatalog weaponCatalog,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    long? generationId = await db.Generations
        .Where(value => value.PublicId == publicId)
        .Select(value => (long?)value.Id)
        .SingleOrDefaultAsync(cancellationToken);
    if (generationId is null) return Results.NotFound();
    GenerationHighlight[] highlights = await db.GenerationHighlights.AsNoTracking()
        .Where(value => value.GenerationId == generationId.Value)
        .OrderByDescending(value => value.TotalScore)
        .ThenBy(value => value.HighlightId)
        .ToArrayAsync(cancellationToken);
    JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    return Results.Ok(highlights.Select(value => new
    {
        id = value.HighlightId,
        value.Type,
        value.RoundNumber,
        value.MapName,
        value.KillCount,
        value.HeadshotCount,
        value.CombatScore,
        value.BeautyScore,
        value.TotalScore,
        value.Recommended,
        value.SelectedByUser,
        value.EstimatedDurationMilliseconds,
        weapons = DeserializeWeapons(value.WeaponSequenceJson, jsonOptions)
            .Select(segment =>
            {
                WeaponMetadata trusted = weaponCatalog.Resolve(segment.WeaponCode);
                return new
                {
                    trusted.Code,
                    trusted.DisplayName,
                    trusted.IconPath,
                    segment.KillCount,
                    segment.SwapBefore
                };
            })
    }));
});
app.MapGet("/api/generations/{publicId}/events", async (
    string publicId,
    long? after,
    IDbContextFactory<GenerationDbContext> factory,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    bool exists = await db.Generations.AnyAsync(
        value => value.PublicId == publicId, cancellationToken);
    if (!exists) return Results.NotFound();
    var events = await db.GenerationEvents.AsNoTracking()
        .Where(value =>
            value.Generation.PublicId == publicId &&
            value.Id > (after ?? 0))
        .OrderBy(value => value.Id)
        .Take(100)
        .Select(value => new
        {
            value.Id,
            value.Level,
            value.Stage,
            value.Message,
            value.ProgressPercent,
            value.CreatedAt
        })
        .ToArrayAsync(cancellationToken);
    return Results.Ok(events);
});
app.MapGet("/generations/{publicId}/video", async (
    string publicId,
    bool? download,
    IDbContextFactory<GenerationDbContext> factory,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db = await factory.CreateDbContextAsync(cancellationToken);
    GenerationArtifact? artifact = await db.GenerationArtifacts
        .Where(value => value.Generation.PublicId == publicId && value.Type == ArtifactType.FinalVideo)
        .SingleOrDefaultAsync(cancellationToken);
    if (artifact is null || !File.Exists(artifact.StoredPath)) return Results.NotFound();
    string? fileName = download == true ? $"cs2-highlights-{publicId}.mp4" : null;
    return Results.File(
        artifact.StoredPath,
        "video/mp4",
        fileName,
        enableRangeProcessing: true);
});
app.Run();

static WeaponSequenceSegment[] DeserializeWeapons(
    string json,
    JsonSerializerOptions options)
{
    try
    {
        return JsonSerializer.Deserialize<WeaponSequenceSegment[]>(json, options) ?? [];
    }
    catch (JsonException)
    {
        return [];
    }
}

public partial class Program;
