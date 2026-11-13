using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Application.Llm;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Web.Authentication;
using DevOpsAgent.Web;
using DevOpsAgent.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDevOpsPersistence(builder.Configuration);
builder.Services.AddScoped<ProviderService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<ServerService>();
builder.Services.AddScoped<ProviderStatusState>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
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

app.MapStaticAssets().Add(endpointBuilder =>
    endpointBuilder.Metadata.Add(new AllowAnonymousAttribute()));
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
