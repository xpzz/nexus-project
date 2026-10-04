using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Core.Errors;

namespace Nexus.Collectors.Graph;

public sealed class GraphException(HttpStatusCode status, NexusError error) : Exception(error.ToString())
{
    public HttpStatusCode Status { get; } = status;

    public NexusError Error { get; } = error;
}

/// <summary>Translates Microsoft Entra / Graph errors into "what happened, impact, how to fix" (SPEC §4.8).</summary>
public static partial class GraphErrors
{
    public static NexusError FromToken(string body)
    {
        var text = Describe(body);
        var match = AadCode().Match(text);
        if (match.Success && ErrorCatalog.Entra.TryGetValue(match.Value, out var known))
        {
            return known.WithDetail(text);
        }

        return new NexusError("NEXUS-AZ-001", "Não foi possível obter o token do Microsoft Entra ID.",
            "A coleta do Intune e do Entra ID não roda; o último resultado válido é mantido.",
            "Rode Configurar-Azure.cmd -ValidateOnly no servidor para ver o motivo e corrigir.").WithDetail(text);
    }

    /// <summary>403 on an optional governance read: names the permission (and the license when one is needed) instead of a generic denial.</summary>
    public static NexusError MissingPermission(string feature, string permission, string? license, string path, string body) =>
        new NexusError("NEXUS-AZ-403G", $"O Microsoft Graph negou a leitura de {feature}.",
            $"{feature} ficam sem coleta; o restante do inventário continua normal e o último resultado válido é mantido.",
            $"No aplicativo 'Azul Nexus – Coletor', conceda a permissão de aplicativo {permission} com consentimento do administrador (rode Configurar-Azure.cmd)." + (license is null ? "" : $" {license}"))
            .WithDetail($"{path}: {Describe(body)}");

    public static NexusError FromGraph(HttpStatusCode status, string body, string path)
    {
        var text = Describe(body);
        if (status == HttpStatusCode.Forbidden)
        {
            return ErrorCatalog.Entra["Graph403"].WithDetail($"{path}: {text}");
        }

        return new NexusError($"NEXUS-AZ-{(int)status}", $"O Microsoft Graph respondeu {(int)status} em {path}.",
            "Os dados dessa consulta não foram atualizados; o último resultado válido é mantido.",
            "Se persistir, veja o log do Worker e rode Configurar-Azure.cmd -ValidateOnly.").WithDetail(text);
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    return $"{error.GetProperty("code").GetString()}: {error.GetProperty("message").GetString()}";
                }

                return $"{error.GetString()}: {(root.TryGetProperty("error_description", out var d) ? d.GetString() : "")}";
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        return body.Length > 300 ? body[..300] : body;
    }

    [GeneratedRegex(@"AADSTS\d{5,6}")]
    private static partial Regex AadCode();
}
