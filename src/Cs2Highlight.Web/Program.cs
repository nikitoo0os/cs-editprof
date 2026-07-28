using System.Threading.RateLimiting;
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
    options.UseSqlite(builder.Configuration.GetConnectionString("GenerationDb") ??
        "Data Source=storage/generations.db"));
UploadOptions uploadOptions = builder.Configuration.GetSection("Uploads").Get<UploadOptions>() ?? new();
StorageOptions storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
PipelineOptions pipelineOptions = builder.Configuration.GetSection("Pipeline").Get<PipelineOptions>() ?? new();
RetentionOptions retentionOptions = builder.Configuration.GetSection("Retention").Get<RetentionOptions>() ?? new();
builder.Services.AddSingleton(uploadOptions);
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(pipelineOptions);
builder.Services.AddSingleton(retentionOptions);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadOptions.MaximumTotalSizeBytes;
    options.MemoryBufferThreshold = 64 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = uploadOptions.MaximumTotalSizeBytes);
builder.Services.AddSingleton<GenerationStorage>();
builder.Services.AddSingleton<DemoUploadService>();
builder.Services.AddSingleton<GenerationWakeSignal>();
builder.Services.AddSingleton<GenerationCancellationRegistry>();
builder.Services.AddSingleton<GlobalHighlightSelector>();
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
        "default-src 'self'; script-src 'self' https://cdnjs.cloudflare.com; style-src 'self' 'unsafe-inline'; media-src 'self'; connect-src 'self' ws: wss:";
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

public partial class Program;
