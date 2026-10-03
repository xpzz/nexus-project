using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Nexus.Data;
using Nexus.Data.Support;

namespace Nexus.Web.Setup;

/// <summary>
/// Before SSO exists the Nexus is in setup mode (SPEC §4.1): open on the server itself (localhost),
/// remote access only after redeeming the one-time code shown at the end of the installation.
/// </summary>
public sealed class SetupAccessMiddleware(RequestDelegate next)
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string AccessPath = "/configuracao/acesso";
    public const string SetupRole = "Nexus.Setup";

    private static readonly string[] AlwaysAllowed = ["/healthz", AccessPath, "/_framework", "/_content", "/app.css", "/favicon"];

    public async Task InvokeAsync(HttpContext context, INexusDbFactory dbFactory, SettingsProvider settings)
    {
        var path = context.Request.Path.Value ?? "/";
        if (AlwaysAllowed.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (!settings.Exists)
        {
            await WritePlainAsync(context, HttpStatusCode.ServiceUnavailable,
                "O Azul Nexus ainda não foi configurado. Execute Install-AzulNexus.ps1 no servidor.");
            return;
        }

        bool setupMode;
        try
        {
            await using var db = dbFactory.Create();
            setupMode = (await SetupModeService.GetAsync(db, context.RequestAborted)).SetupModeActive;
        }
        catch (Exception)
        {
            await WritePlainAsync(context, HttpStatusCode.ServiceUnavailable,
                "Banco do Nexus indisponível. Impacto: a interface não funciona. Como resolver: no servidor, rode 'nexusctl test' e siga as orientações.");
            return;
        }

        if (setupMode)
        {
            if (IsLocal(context) || context.User.IsInRole(SetupRole))
            {
                await next(context);
                return;
            }

            context.Response.Redirect(AccessPath);
            return;
        }

        // SSO with Microsoft Entra ID is delivered in increment 0.6; until then access stays closed.
        await WritePlainAsync(context, HttpStatusCode.Forbidden,
            "O modo de configuração foi encerrado e o SSO ainda não está disponível nesta versão. " +
            "Como resolver: no servidor, como administrador, execute 'nexusctl recover-access'.");
    }

    public static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is null || IPAddress.IsLoopback(remote);
    }

    public static Task SignInAsync(HttpContext context) =>
        context.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "setup"), new Claim(ClaimTypes.Role, SetupRole)], Scheme)));

    private static async Task WritePlainAsync(HttpContext context, HttpStatusCode status, string message)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message);
    }
}
