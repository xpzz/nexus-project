using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Nexus.Collectors.Graph;
using Nexus.Core.Configuration;

namespace Nexus.Web.Setup;

public enum EntraDecision { Allow, Challenge, NoRole, NeedsIntegrationAdmin, Closed }

/// <summary>Who gets in when sign-in with Entra ID is on (ADR-0008). Pure, so it can be tested without a tenant.</summary>
public static class EntraPolicy
{
    public static EntraDecision Decide(string path, bool isAuthenticated, IReadOnlySet<string> roles, bool hasSetupRole)
    {
        if (hasSetupRole)
        {
            return EntraDecision.Allow;
        }

        if (!isAuthenticated)
        {
            return EntraDecision.Challenge;
        }

        if (!NexusRoles.All.Any(roles.Contains))
        {
            return EntraDecision.NoRole;
        }

        return AccessPolicy.IsOperatorOnly(path) && !roles.Contains(NexusRoles.IntegrationAdmin) ? EntraDecision.NeedsIntegrationAdmin : EntraDecision.Allow;
    }
}

/// <summary>Sign-in with Microsoft Entra ID: authorization code with PKCE, the client authenticates with the web app's certificate (no secret anywhere).</summary>
public static class EntraSignIn
{
    public const string PolicyScheme = "Nexus";
    public const string SessionScheme = "Nexus.Session";
    public const string ChallengeScheme = "Nexus.Entra";

    public sealed record Setup(string TenantId, string ClientId, string CallbackPath, string SignedOutPath, X509Certificate2 Certificate);

    /// <summary>Null with an explanation (what happened, impact, how to fix) when the Azure step is incomplete or the certificate is not readable.</summary>
    public static (Setup? Setup, string? Problem) Prepare(AzureSettings? azure)
    {
        if (azure is null || string.IsNullOrWhiteSpace(azure.Web.ClientId) || string.IsNullOrWhiteSpace(azure.Web.CertificateThumbprint))
        {
            return (null, "O login com o Entra ID está ligado (Web.AuthMode = entra), mas o registro 'Azul Nexus – Web' não foi encontrado em config/azure.json. Impacto: ninguém consegue entrar. Como resolver: rode Configurar-Azure.cmd como administrador global ou volte Web.AuthMode para open.");
        }

        var certificate = Find(azure.Web.CertificateThumbprint);
        if (certificate is null)
        {
            return (null, $"O certificado do aplicativo web ({azure.Web.CertificateThumbprint}) não está legível no repositório do computador. Impacto: o login com o Entra ID não funciona. Como resolver: rode Configurar-Azure.cmd de novo e confirme que a conta do serviço pode ler a chave privada.");
        }

        var callback = Uri.TryCreate(azure.Web.RedirectUri, UriKind.Absolute, out var redirect) ? redirect.AbsolutePath : "/signin-oidc";
        var signedOut = Uri.TryCreate(azure.Web.LogoutUri, UriKind.Absolute, out var logout) ? logout.AbsolutePath : "/signout-callback-oidc";
        return (new Setup(azure.TenantId, azure.Web.ClientId, callback, signedOut, certificate), null);
    }

    private static X509Certificate2? Find(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                if (found.Count > 0 && found[0].HasPrivateKey)
                {
                    return found[0];
                }
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // A store that cannot be opened (no permission, not supported on this OS) just does not have the certificate.
            }
        }

        return null;
    }

    public static void Register(AuthenticationBuilder auth, Setup setup)
    {
        auth.AddPolicyScheme(PolicyScheme, "Nexus", o =>
            o.ForwardDefaultSelector = ctx => ctx.Request.Cookies.ContainsKey("AzulNexus.Setup") ? SetupAccessMiddleware.Scheme : SessionScheme);
        auth.AddCookie(SessionScheme, o =>
        {
            o.Cookie.Name = "AzulNexus.Session";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax; // the redirect back from Microsoft is a cross-site navigation
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = false;
        });
        auth.AddOpenIdConnect(ChallengeScheme, o =>
        {
            o.Authority = $"https://login.microsoftonline.com/{setup.TenantId}/v2.0";
            o.ClientId = setup.ClientId;
            o.SignInScheme = SessionScheme;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.UsePkce = true;
            o.CallbackPath = setup.CallbackPath;
            o.SignedOutCallbackPath = setup.SignedOutPath;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.MapInboundClaims = false;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = "roles";
            o.Events = new OpenIdConnectEvents
            {
                OnAuthorizationCodeReceived = context =>
                {
                    context.TokenEndpointRequest!.ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                    context.TokenEndpointRequest.ClientAssertion = GraphAssertion.Create(setup.Certificate, setup.TenantId, setup.ClientId, DateTimeOffset.UtcNow);
                    return Task.CompletedTask;
                },
                OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/erro");
                    return Task.CompletedTask;
                },
            };
        });
    }
}

/// <summary>Whether sign-in with Entra ID is active, and when it was requested but could not start, why (shown to anyone who is not the server operator).</summary>
public sealed record EntraStatus(bool Active, string? Problem);
