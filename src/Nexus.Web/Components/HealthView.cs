using Nexus.Core.Errors;
using Nexus.Data.Entities;

namespace Nexus.Web.Components;

public static class HealthView
{
    public static NexusError? ToError(HealthResultRecord record) =>
        record.ErrorCode is null || record.Status is not ("Error" or "NotConfigured")
            ? null
            : new NexusError(record.ErrorCode, record.Message, record.Impact ?? "", record.HowToFix ?? "", record.Script);

    public static string Label(string status) => status switch
    {
        "Ok" => "OK",
        "Warning" => "Atenção",
        "Error" => "Com erro",
        "NotConfigured" => "Não configurado",
        "NotEnabled" => "Não habilitado",
        _ => status,
    };

    public static string Css(string? status) => status switch
    {
        "Ok" or "Concluída" => "ok",
        "Warning" or "Adiada" => "warn",
        "Error" or "Falhou" => "error",
        _ => "muted",
    };
}
