using Dvc.Api.Infrastructure;
using Dvc.Api.Infrastructure.Data;
using Dvc.Api.Endpoints;
using Dvc.Api.Models.Definitions;
using Dvc.Api.Models.Entities;
using Dvc.Api.Models.Options;
using Dvc.Api.Services;
using Dvc.Core.Definitions;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

// A coarse failsafe against a runaway caller, not real rate limiting.
const int MaxConcurrentConnections = 100;

builder.WebHost.ConfigureKestrel(options =>
{
    // The only body cap that applies: uploads are parsed with
    // MultipartReader, not the form reader.
    options.Limits.MaxRequestBodySize = UploadDefinitions.MaxUploadBytes;
    options.Limits.MaxConcurrentConnections = MaxConcurrentConnections;
});

builder.Services.AddOpenApi();

// Enums as names (e.g. "Completed") rather than integers, so callers don't
// depend on declaration order.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Every error response is problem details (RFC 9457).
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<DvcDbContext>(options =>
    options.UseSqlite(connectionString)
);

// Validated at startup, so bad config fails fast instead of as a failed
// encode. The preset is normalized to the table's spelling, since it goes
// straight to ffmpeg; an unknown one is left for Validate to reject.
builder.Services.AddOptions<EncodingOptions>()
    .Bind(builder.Configuration.GetSection(EncodingOptions.SectionName))
    .PostConfigure(o => o.Preset = EncoderPresetDefinitions.Presets.CanonicalKey(o.Preset) ?? o.Preset)
    .Validate(o => EncoderPresetDefinitions.Presets.ContainsKey(o.Preset),
        $"Encoding:Preset must be one of: {string.Join(", ", EncoderPresetDefinitions.Presets.Keys)}.")
    .Validate(o => o.DefaultMaxShortSide is null or > 0,
        "Encoding:DefaultMaxShortSide must be greater than zero, or empty to disable the cap.")
    .Validate(o => o.DefaultMaxFps is null or > 0,
        "Encoding:DefaultMaxFps must be greater than zero, or empty to disable the cap.")
    .ValidateOnStart();

builder.Services.AddScoped<IJobSubmissionService, JobSubmissionService>();
builder.Services.AddScoped<IVideoCompressionService, VideoCompressionService>();
builder.Services.AddSingleton<IJobQueue, JobQueue>();
builder.Services.AddHostedService<JobProcessingWorker>();

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.UseUtcTimestamp = true;
});

var app = builder.Build();

// Problem details for errors the endpoints don't return themselves:
// unhandled exceptions, and framework errors without a body, such as an
// unknown route.
app.UseExceptionHandler();
app.UseStatusCodePages();

// Off by default, so local development applies migrations explicitly with
// dotnet ef. The container turns it on, since its runtime image has no
// dotnet-ef. Runs before the job recovery below, which queries the Jobs
// table.
if (builder.Configuration.GetValue("Database:AutoMigrate", defaultValue: false))
{
    using var migrationScope = app.Services.CreateScope();
    migrationScope.ServiceProvider.GetRequiredService<DvcDbContext>().Database.Migrate();
}

// WAL lets status reads proceed while the worker writes. journal_mode
// persists in the database file; synchronous=NORMAL is SQLite's
// recommended pairing with WAL.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DvcDbContext>();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
}

// Recovers jobs an unclean shutdown left mid-flight. Queued jobs were lost
// with the in-memory queue; processing ones were killed mid-encode, so
// their partial output is deleted and they're reset to queued. Both are
// re-enqueued in submission order before the worker starts.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DvcDbContext>();
    var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
    var recoveryLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Ordered client-side: SQLite can't translate ORDER BY on DateTimeOffset.
    var orphanedJobs = (await db.Jobs
        .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Processing)
        .ToListAsync())
        .OrderBy(j => j.CreatedAt)
        .ToList();

    foreach (var job in orphanedJobs)
    {
        if (job.Status == JobStatus.Processing)
        {
            if (job.OutputPath is not null)
            {
                File.Delete(job.OutputPath);
            }

            job.Status = JobStatus.Queued;
            job.OutputPath = null;
            job.OutputWidth = null;
            job.OutputHeight = null;
            job.OutputFps = null;
        }
    }

    if (orphanedJobs.Count > 0)
    {
        await db.SaveChangesAsync();
        recoveryLogger.LogWarning(
            "Recovered {Count} job(s) orphaned by an unclean shutdown: {JobIds}",
            orphanedJobs.Count, string.Join(", ", orphanedJobs.Select(j => j.Id)));

        foreach (var job in orphanedJobs)
        {
            jobQueue.Enqueue(job.Id);
        }
    }
}

// The API description (/openapi/v1.json) and its interactive UI (/scalar).
// Off unless configured, so a deployment only serves them if an operator
// turns them on; appsettings.Development.json turns them on locally.
if (app.Configuration.GetValue("OpenApi:Enabled", defaultValue: false))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthEndpoints();

// Versioned, so a breaking change can ship as /v2 alongside. /health and
// /openapi describe the deployment rather than the API, so they aren't.
// Every endpoint can return a 500 from the exception handler, so it's
// declared once for the OpenAPI document.
var v1 = app.MapGroup("/v1")
    .ProducesProblem(StatusCodes.Status500InternalServerError);
v1.MapJobEndpoints();
v1.MapOptionsEndpoints();

app.Run();