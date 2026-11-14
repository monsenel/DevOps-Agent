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

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDevOpsPersistence(builder.Configuration);
builder.Services.AddScoped<ProviderService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<AgentContextBuilder>();
builder.Services.AddScoped<AgentToolDispatcher>();
builder.Services.AddScoped<UpdatesService>();
builder.Services.AddScoped<MetricService>();
builder.Services.AddScoped<RollupService>();
builder.Services.AddScoped<HealthCheckService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<IAlertNotificationService>(provider => provider.GetRequiredService<NotificationService>());
builder.Services.AddScoped<ServerService>();
builder.Services.AddScoped<ServerCapabilityService>();
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddScoped<DriftService>();
builder.Services.AddScoped<DiscoveryScheduleService>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddScoped<ExecutionModeService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<ActionExecutionService>();
builder.Services.AddSingleton<ToolCommandBuilder>();
builder.Services.AddScoped<GraphBuilder>();
builder.Services.AddScoped<ProviderStatusState>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<AlertStatusState>();
builder.Services.AddHostedService<DiscoveryScheduler>();
builder.Services.AddHostedService<MetricPollerService>();
builder.Services.AddHostedService<MetricRollupHostedService>();
builder.Services.AddHostedService<HealthCheckHostedService>();
builder.Services.AddHostedService<AlertEngineHostedService>();
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

var app = builder.Build();

_ = app.Services.GetRequiredService<AdminCredentialVerifier>();
await app.Services.ApplyMigrationsAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
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
