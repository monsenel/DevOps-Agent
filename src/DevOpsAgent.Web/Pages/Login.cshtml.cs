using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DevOpsAgent.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DevOpsAgent.Web.Pages;

[AllowAnonymous]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(LoginRateLimit.PolicyName)]
public sealed class LoginModel(AdminCredentialVerifier credentialVerifier) : PageModel
{
    [BindProperty]
    [Required]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Enter both your username and password.";
            return Page();
        }

        if (!credentialVerifier.IsConfigured)
        {
            ErrorMessage = "Admin credentials are not configured. Set ADMIN_USER and a password in the environment.";
            return Page();
        }

        if (!credentialVerifier.Verify(Username, Password))
        {
            ErrorMessage = "The username or password is incorrect.";
            return Page();
        }

        var claims = new[] { new Claim(ClaimTypes.Name, Username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        return !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : Redirect("/");
    }
}
