using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Llm;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Web.Authentication;
using DevOpsAgent.Web;
using DevOpsAgent.Web.Components;
using DevOpsAgent.Web.Hosted;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDevOpsPersistence(builder.Configuration);
builder.Services.AddScoped<ProviderService>();
builder.Services.AddScoped<LlmRoutingService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<ToolCallLimitService>();
builder.Services.AddScoped<AgentContextBuilder>();
builder.Services.AddScoped<AgentToolDispatcher>();
builder.Services.AddScoped<UpdatesService>();
builder.Services.AddScoped<UpdateScheduleService>();
builder.Services.AddScoped<RollingUpdatePlanner>();
builder.Services.AddScoped<DevOpsAgent.Application.Security.SecurityAuditService>();
builder.Services.AddScoped<DevOpsAgent.Application.Security.CveScanService>();
builder.Services.AddScoped<DevOpsAgent.Application.Security.FileIntegrityService>();
builder.Services.AddSingleton<RunbookService>();
builder.Services.AddScoped<ImpactService>();
builder.Services.AddSingleton(builder.Configuration.GetSection(ConfigEditOptions.SectionName).Get<ConfigEditOptions>() ?? new ConfigEditOptions());
builder.Services.AddScoped<ConfigEditService>();
builder.Services.AddScoped<DevOpsAgent.Application.Search.GlobalSearchService>();
builder.Services.AddScoped<MetricService>();
builder.Services.AddScoped<ContainerRestartMonitor>();
builder.Services.AddScoped<RollupService>();
builder.Services.AddScoped<AnomalyBaselineService>();
builder.Services.AddScoped<IncidentModeService>();
builder.Services.AddHttpClient<ExternalObservabilityService>(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<HealthCheckService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<DailyDigestService>();
builder.Services.AddSingleton<AlertNotificationQueue>();
// Alert evaluation only queues notifications; AlertNotificationDispatcher sends them, so a slow endpoint never stalls it (T7.R7).
builder.Services.AddScoped<IAlertNotificationService>(provider => provider.GetRequiredService<AlertNotificationQueue>());
builder.Services.AddScoped<ServerService>();
builder.Services.AddScoped<ServerCapabilityService>();
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddSingleton<IDiscoveryScopeRunner, HostDiscoveryScopeRunner>();
builder.Services.AddSingleton<IDiscoveryCoordinator, DiscoveryCoordinator>();
builder.Services.AddScoped<DriftService>();
builder.Services.AddScoped<DiscoveryScheduleService>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddScoped<DiscoveryTriggerState>();
builder.Services.AddScoped<ExecutionModeService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddSingleton<IOperatorAddressRegistry, DevOpsAgent.Infrastructure.Security.OperatorAddressRegistry>();
builder.Services.AddSingleton<IProtectedAddressSource, DevOpsAgent.Infrastructure.Security.ProtectedAddressSource>();
builder.Services.AddScoped<BanTargetGuard>();
builder.Services.AddScoped<ActionExecutionService>();
builder.Services.AddSingleton<ToolCommandBuilder>();
builder.Services.AddScoped<GraphBuilder>();
builder.Services.AddScoped<LlmEdgeSuggestionService>();
builder.Services.AddScoped<ProviderStatusState>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<AlertStatusState>();
builder.Services.AddSingleton(builder.Configuration.GetSection(MonitoringCycleOptions.SectionName).Get<MonitoringCycleOptions>() ?? new MonitoringCycleOptions());
builder.Services.AddHostedService<DiscoveryScheduler>();
builder.Services.AddHostedService<UpdateScanHostedService>();
builder.Services.AddHostedService<MetricPollerService>();
builder.Services.AddHostedService<MetricRollupHostedService>();
builder.Services.AddHostedService<HealthCheckHostedService>();
builder.Services.AddHostedService<AlertEngineHostedService>();
builder.Services.AddHostedService<AlertNotificationDispatcher>();
builder.Services.AddHostedService<DailyDigestHostedService>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRazorPages();
builder.Services.Configure<AdminAuthenticationOptions>(options =>
{
    options.User = builder.Configuration[AdminAuthenticationOptions.UserConfigurationKey];
    options.PasswordHash = builder.Configuration[AdminAuthenticationOptions.PasswordHashConfigurationKey];
    options.Password = builder.Configuration[AdminAuthenticationOptions.PasswordConfigurationKey];
});
builder.Services.AddSingleton<AdminCredentialVerifier>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = ".DevOpsAgent.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddCascadingAuthenticationState();
// Login attempts (POST /login) are limited per client address; viewing the page is not.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(LoginRateLimit.PolicyName, context => HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = LoginRateLimit.AttemptsPerWindow, Window = LoginRateLimit.Window, QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("view"));
});

var app = builder.Build();

_ = app.Services.GetRequiredService<AdminCredentialVerifier>();
await app.Services.ApplyMigrationsAsync();

// Actions left Running by a crash or restart can never finish; mark them Failed instead of leaving them stuck.
try
{
    await using var recoveryScope = app.Services.CreateAsyncScope();
    var interrupted = await recoveryScope.ServiceProvider.GetRequiredService<ActionExecutionService>()
        .RecoverInterruptedAsync(CancellationToken.None);
    if (interrupted > 0) app.Logger.LogWarning("Marked {InterruptedActions} interrupted action(s) as failed.", interrupted);
}
catch (Exception exception)
{
    app.Logger.LogError(exception, "Could not sweep interrupted actions.");
}

// Likewise, a discovery run left Running can never finish.
try
{
    var interruptedRuns = await app.Services.RecoverInterruptedDiscoveryAsync(TimeProvider.System);
    if (interruptedRuns > 0) app.Logger.LogWarning("Marked {InterruptedRuns} interrupted discovery run(s) as failed.", interruptedRuns);
}
catch (Exception exception)
{
    app.Logger.LogError(exception, "Could not sweep interrupted discovery runs.");
}

// Configure the HTTP request pipeline.
// Once the host is stopping, no new Blazor circuit connection is accepted (T7.R17). A browser tab reconnects the moment the
// server closes its connection; Kestrel would accept that WebSocket and then wait for it until the host's shutdown timeout
// (30 s), so an open tab alone made SIGTERM take that long.
app.Use(async (context, next) =>
{
    if (app.Lifetime.ApplicationStopping.IsCancellationRequested && context.Request.Path.StartsWithSegments("/_blazor"))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    await next();
});
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment())
{
    // Development-only pages (the component preview) do not exist elsewhere.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/dev")) context.Response.StatusCode = StatusCodes.Status404NotFound;
        else await next();
    });
}
app.UseAuthentication();
app.UseAuthorization();
// block_ip must never ban the operator (D-030): remember the client addresses of authenticated requests.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true && context.Connection.RemoteIpAddress is { } client)
        context.RequestServices.GetRequiredService<IOperatorAddressRegistry>().Record(client);
    await next();
});
app.UseRateLimiter();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
})).AllowAnonymous();

app.MapGet("/api/graph/{snapshotId:guid}", async Task<IResult> (
    Guid snapshotId,
    string? view,
    IGraphRepository graphRepository,
    CancellationToken cancellationToken) =>
{
    var graphView = GraphView.Service;
    if (view is not null && (!Enum.TryParse(view, ignoreCase: true, out graphView) || !Enum.IsDefined(graphView)))
    {
        return Results.BadRequest(new { error = "View must be 'service' or 'network'." });
    }

    var graph = await graphRepository.GetAsync(snapshotId, graphView, cancellationToken);
    return graph is null ? Results.NotFound() : Results.Ok(graph);
});

app.MapStaticAssets().Add(endpointBuilder =>
    endpointBuilder.Metadata.Add(new AllowAnonymousAttribute()));
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
