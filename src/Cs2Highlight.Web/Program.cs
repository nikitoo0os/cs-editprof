using System.Threading.RateLimiting;
using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Hubs;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    "appsettings.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = !builder.Environment.IsDevelopment();
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
    .AddEntityFrameworkStores<GenerationDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddAuthorization(options =>
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin")));
builder.Services.AddSignalR();
builder.Services.AddDbContextFactory<GenerationDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("GenerationDb") ??
            "Data Source=storage/generations.db",
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
UploadOptions uploadOptions = builder.Configuration.GetSection("Uploads").Get<UploadOptions>() ?? new();
StorageOptions storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
PipelineOptions pipelineOptions = builder.Configuration.GetSection("Pipeline").Get<PipelineOptions>() ?? new();
CinematicCameraRuntimeOptions cinematicCameraOptions =
    builder.Configuration.GetSection("CinematicCameraRuntime")
        .Get<CinematicCameraRuntimeOptions>() ?? new();
RetentionOptions retentionOptions = builder.Configuration.GetSection("Retention").Get<RetentionOptions>() ?? new();
MusicUploadOptions musicUploadOptions =
    builder.Configuration.GetSection("MusicUploads").Get<MusicUploadOptions>() ?? new();
TrustedLutOptions trustedLutOptions =
    builder.Configuration.GetSection("TrustedLuts").Get<TrustedLutOptions>() ?? new();
RecommendedSelectionOptions selectionOptions =
    builder.Configuration.GetSection("RecommendedSelection").Get<RecommendedSelectionOptions>() ?? new();
PaymentOptions paymentOptions =
    builder.Configuration.GetSection("Payments").Get<PaymentOptions>() ?? new();
CommerceOptions commerceOptions =
    builder.Configuration.GetSection("Commerce").Get<CommerceOptions>() ?? new();
LegalOptions legalOptions =
    builder.Configuration.GetSection("Legal").Get<LegalOptions>() ?? new();
builder.Services.AddSingleton(uploadOptions);
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(pipelineOptions);
builder.Services.AddSingleton(cinematicCameraOptions);
builder.Services.AddSingleton(retentionOptions);
builder.Services.AddSingleton(musicUploadOptions);
builder.Services.AddSingleton(trustedLutOptions);
builder.Services.AddSingleton(selectionOptions);
builder.Services.AddSingleton(paymentOptions);
builder.Services.AddSingleton(commerceOptions);
builder.Services.AddSingleton(legalOptions);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadOptions.MaximumTotalSizeBytes;
    options.MemoryBufferThreshold = 64 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = uploadOptions.MaximumTotalSizeBytes);
builder.Services.AddSingleton<GenerationStorage>();
builder.Services.AddSingleton<GenerationMetrics>();
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
builder.Services.AddSingleton<Cs2Highlight.Music.IAutomaticMapCameraCalibrator, Cs2Highlight.Music.AutomaticMapCameraCalibrator>();
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
builder.Services.AddSingleton<AutomaticCameraCalibrationStore>();
builder.Services.AddSingleton(new InteractiveRetimingOptions());
builder.Services.AddSingleton<IInteractiveTimelineDirector, InteractiveTimelineDirector>();
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
builder.Services.AddHttpClient<YooKassaPaymentProvider>(client =>
{
    client.BaseAddress = new Uri(paymentOptions.ApiBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Payment traffic must not silently inherit a desktop proxy (Clash/V2Ray,
    // etc.). A stopped local proxy otherwise makes the purchase form appear to
    // hang and no confirmation_url can be returned to the browser.
    UseProxy = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});
if (!paymentOptions.UsesYooKassa)
    throw new InvalidOperationException("Payments:Provider must be YooKassa.");
builder.Services.AddScoped<IPaymentProvider>(services =>
    services.GetRequiredService<YooKassaPaymentProvider>());
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<TokenPaymentService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
if (builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IEmailSender, DevelopmentEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<GenerationWorker>();
builder.Services.AddHostedService<GenerationCleanupService>();
builder.Services.AddHealthChecks().AddCheck<GenerationReadinessHealthCheck>("pipeline");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("uploads", httpContext =>
    {
        string client = httpContext.User.Identity?.Name ??
            httpContext.Connection.RemoteIpAddress?.ToString() ??
            "anonymous";
        string partitionKey = $"{httpContext.Request.Method}:{client}";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    options.OnRejected = static async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            """
            <!doctype html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Слишком много попыток</title>
              <style>
                :root { color-scheme: dark; font-family: system-ui, sans-serif; }
                body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #090b0e; color: #f2efe8; }
                main { width: min(520px, calc(100% - 40px)); padding: 32px; border: 1px solid #2a353d; border-radius: 14px; background: #141a20; }
                h1 { margin: 0 0 12px; font-size: 26px; }
                p { color: #c0c8ca; line-height: 1.55; }
                a { display: inline-block; margin-top: 12px; padding: 12px 16px; border-radius: 8px; background: #ff623d; color: #1b100d; font-weight: 700; text-decoration: none; }
              </style>
            </head>
            <body>
              <main>
                <h1>Слишком много попыток загрузки</h1>
                <p>Подожди около минуты и повтори загрузку. Обновления страницы больше не расходуют лимит.</p>
                <a href="/">Вернуться к загрузке</a>
              </main>
            </body>
            </html>
            """,
            cancellationToken);
    };
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
    RoleManager<IdentityRole> roleManager =
        scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (string role in new[] { "User", "Admin" })
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }
    TokenPackage[] desiredTokenPackages =
    {
        new TokenPackage { Code = "single", Name = "3 токена", TokenAmount = 3, PriceAmountMinor = 14900, Currency = "RUB", SortOrder = 1 },
        new TokenPackage { Code = "starter", Name = "7 токенов", TokenAmount = 7, PriceAmountMinor = 29900, Currency = "RUB", SortOrder = 2 },
        new TokenPackage { Code = "creator", Name = "15 токенов", TokenAmount = 15, PriceAmountMinor = 54900, Currency = "RUB", SortOrder = 3 }
    };
    TokenPackage[] storedTokenPackages = await db.TokenPackages.ToArrayAsync();
    HashSet<string> desiredPackageCodes = desiredTokenPackages
        .Select(value => value.Code)
        .ToHashSet(StringComparer.Ordinal);
    foreach (TokenPackage stored in storedTokenPackages)
        stored.IsActive = desiredPackageCodes.Contains(stored.Code);
    foreach (TokenPackage package in desiredTokenPackages)
    {
        TokenPackage? stored = storedTokenPackages.SingleOrDefault(
            value => value.Code == package.Code);
        if (stored is null)
        {
            db.TokenPackages.Add(package);
            continue;
        }
        stored.Name = package.Name;
        stored.TokenAmount = package.TokenAmount;
        stored.PriceAmountMinor = package.PriceAmountMinor;
        stored.Currency = package.Currency;
        stored.SortOrder = package.SortOrder;
        stored.IsActive = true;
    }
    DateTimeOffset startupRepairTime = DateTimeOffset.UtcNow;
    GenerationMovieSettings[] settingsMissingRenderLock =
        await db.GenerationMovieSettings
            .Include(value => value.Generation)
            .Where(value =>
                value.LockedAt == null &&
                value.Generation.PaymentStatus == PaymentStatus.Succeeded)
            .ToArrayAsync();
    foreach (GenerationMovieSettings settings in settingsMissingRenderLock)
        settings.LockedAt = settings.Generation.PaidAt ?? startupRepairTime;
    Generation[] interruptedMusicPlanGenerations = await db.Generations
        .Where(value =>
            value.Status == GenerationStatus.Failed &&
            value.ErrorCode == "MUSIC_PLAN_NOT_LOCKED" &&
            value.PaymentStatus == PaymentStatus.Succeeded)
        .ToArrayAsync();
    foreach (Generation generation in interruptedMusicPlanGenerations)
    {
        GenerationStateMachine.Transition(
            generation,
            GenerationStatus.QueuedForGeneration,
            startupRepairTime);
        generation.ErrorCode = null;
        generation.ErrorMessage = null;
        generation.CurrentStage = "Queued after music plan lock repair";
        generation.RetryCount++;
        db.GenerationEvents.Add(new GenerationEvent
        {
            GenerationId = generation.Id,
            Level = "Warning",
            Stage = "MusicPlanLockRepair",
            Message = "Recovered missing movie settings lock after token-flow migration",
            ProgressPercent = generation.ProgressPercent,
            CreatedAt = startupRepairTime
        });
    }
    string? adminEmail = builder.Configuration["Admin:Email"];
    string? adminPassword = builder.Configuration["Admin:Password"];
    if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
    {
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                ReferralCode = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..10]
            };
            IdentityResult result = await userManager.CreateAsync(admin, adminPassword);
            if (!result.Succeeded)
                throw new InvalidOperationException("ADMIN_SEED_FAILED");
        }
        if (!await userManager.IsInRoleAsync(admin, "Admin"))
            await userManager.AddToRoleAsync(admin, "Admin");
    }
    await db.SaveChangesAsync();
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
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapRazorPages();
app.MapHub<GenerationHub>("/hubs/generations");
app.MapTimelineDirectorApi();
app.MapPost("/api/payments/yookassa", async (
    YooKassaNotification notification,
    PaymentService payments,
    TokenPaymentService tokenPayments,
    CancellationToken cancellationToken) =>
{
    string? providerPaymentId = notification.Payload?.Id;
    if (notification.Event is not ("payment.succeeded" or "payment.canceled") ||
        string.IsNullOrWhiteSpace(providerPaymentId))
    {
        return Results.BadRequest();
    }

    // The notification body is never trusted as proof of payment. The service
    // requests the current payment state directly from YooKassa before updating the order.
    bool generationPayment = await payments.RefreshByProviderPaymentIdAsync(
        providerPaymentId, cancellationToken);
    if (!generationPayment)
        await tokenPayments.RefreshByProviderPaymentIdAsync(
            providerPaymentId, cancellationToken);
    return Results.Ok();
});
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/api/generations/{publicId}", async (
    string publicId,
    IDbContextFactory<GenerationDbContext> factory,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    Generation? generation = await db.Generations.AsNoTracking()
        .SingleOrDefaultAsync(
            value => value.PublicId == publicId,
            cancellationToken);
    if (generation is null || !GenerationAccess.CanRead(generation, context.User, app.Environment))
        return Results.NotFound();
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
    bool hasCameraOnlyVideo = completed &&
        await db.GenerationArtifacts.AnyAsync(
            value => value.GenerationId == generation.Id &&
                value.Type == ArtifactType.CameraOnlyVideo,
            cancellationToken);
    GenerationDemo? activeDemo = generation.Status is
        GenerationStatus.AwaitingPlayerSelection or
        GenerationStatus.AwaitingHighlightSelection
            ? await db.GenerationDemos.AsNoTracking()
                .Where(value =>
                    value.GenerationId == generation.Id &&
                    value.AnalysisStatus == DemoAnalysisStatus.Succeeded &&
                    !value.HighlightSelectionCompleted)
                .OrderBy(value => value.UploadOrder)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
    string demoQuery = activeDemo is null
        ? string.Empty
        : $"?demoId={activeDemo.Id}";
    string? actionUrl = generation.Status switch
    {
        GenerationStatus.AwaitingPlayerSelection =>
            $"/generations/{publicId}/player{demoQuery}",
        GenerationStatus.AwaitingHighlightSelection =>
            $"/generations/{publicId}/highlights{demoQuery}",
        GenerationStatus.AwaitingMusicUpload or
        GenerationStatus.AwaitingMovieConfiguration =>
            $"/generations/{publicId}/music",
        _ => null
    };
    IReadOnlyList<GenerationStageView> stages = GenerationStageMapping.For(
        generation.Status, generation.ActiveStageKey);
    string? activeStageKey = stages.FirstOrDefault(value =>
        value.State is GenerationStageState.Current or GenerationStageState.Failed)?.Key;
    return Results.Ok(new
    {
        publicId,
        status = generation.Status.ToString(),
        stage = generation.CurrentStage,
        activeStageKey,
        stages = stages.Select(value => new
        {
            key = value.Key,
            label = value.Label,
            state = value.State.ToString().ToLowerInvariant()
        }),
        generation.ProgressPercent,
        demoCount,
        playerCount,
        highlightCount,
        generation.ErrorCode,
        generation.ErrorMessage,
        completed,
        actionUrl,
        videoUrl = completed ? $"/generations/{publicId}/video" : null,
        cameraOnlyVideoUrl = hasCameraOnlyVideo
            ? $"/generations/{publicId}/video/cameras"
            : null,
        events
    });
});
app.MapGet("/api/generations/{publicId}/highlights", async (
    string publicId,
    IDbContextFactory<GenerationDbContext> factory,
    IWeaponCatalog weaponCatalog,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    long? generationId = await db.Generations
        .Where(value => value.PublicId == publicId)
        .Select(value => (long?)value.Id)
        .SingleOrDefaultAsync(cancellationToken);
    if (generationId is null) return Results.NotFound();
    Generation? owner = await db.Generations.AsNoTracking()
        .SingleOrDefaultAsync(value => value.Id == generationId.Value, cancellationToken);
    if (owner is null || !GenerationAccess.CanRead(owner, context.User, app.Environment))
        return Results.NotFound();
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
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    Generation? owner = await db.Generations.AsNoTracking().SingleOrDefaultAsync(
        value => value.PublicId == publicId, cancellationToken);
    if (owner is null || !GenerationAccess.CanRead(owner, context.User, app.Environment))
        return Results.NotFound();
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
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db = await factory.CreateDbContextAsync(cancellationToken);
    Generation? owner = await db.Generations.AsNoTracking().SingleOrDefaultAsync(
        value => value.PublicId == publicId, cancellationToken);
    if (owner is null || !GenerationAccess.CanRead(owner, context.User, app.Environment))
        return Results.NotFound();
    if (owner.ExpiresAtUtc is not null && owner.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        return Results.NotFound();
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
app.MapGet("/generations/{publicId}/video/cameras", async (
    string publicId,
    bool? download,
    IDbContextFactory<GenerationDbContext> factory,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    Generation? owner = await db.Generations.AsNoTracking()
        .SingleOrDefaultAsync(
            value => value.PublicId == publicId,
            cancellationToken);
    if (owner is null ||
        !GenerationAccess.CanRead(owner, context.User, app.Environment) ||
        owner.ExpiresAtUtc is not null &&
        owner.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        return Results.NotFound();
    GenerationArtifact? artifact = await db.GenerationArtifacts
        .AsNoTracking()
        .SingleOrDefaultAsync(
            value => value.GenerationId == owner.Id &&
                value.Type == ArtifactType.CameraOnlyVideo,
            cancellationToken);
    if (artifact is null || !File.Exists(artifact.StoredPath))
        return Results.NotFound();
    string? fileName = download == true
        ? $"cs2-cameras-{publicId}.mp4"
        : null;
    return Results.File(
        artifact.StoredPath,
        "video/mp4",
        fileName,
        enableRangeProcessing: true);
});
app.MapGet("/generations/{publicId}/music-audio", async (
    string publicId,
    IDbContextFactory<GenerationDbContext> factory,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    await using GenerationDbContext db =
        await factory.CreateDbContextAsync(cancellationToken);
    Generation? owner = await db.Generations.AsNoTracking().SingleOrDefaultAsync(
        value => value.PublicId == publicId, cancellationToken);
    if (owner is null || !GenerationAccess.CanRead(owner, context.User, app.Environment))
        return Results.NotFound();
    GenerationMusic? music = await db.GenerationMusic.AsNoTracking()
        .SingleOrDefaultAsync(
            value =>
                value.Generation.PublicId == publicId &&
                value.RightsConfirmed,
            cancellationToken);
    if (music is null || !File.Exists(music.StoredPath))
        return Results.NotFound();
    return Results.File(
        music.StoredPath,
        music.ContentType,
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
