using System.Globalization;
using Nexus.Reconciliation;

namespace Nexus.Web.Components;

public sealed record Seg(string Label, int Count, string Css, string? Href = null);

public static class Fmt
{
    private static readonly CultureInfo Br = new("pt-BR");

    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null)
        {
            return "—";
        }

        var span = now - at.Value;
        if (span < TimeSpan.FromHours(1))
        {
            return "agora";
        }

        if (span < TimeSpan.FromHours(24))
        {
            return $"há {(int)span.TotalHours} h";
        }

        var days = (int)span.TotalDays;
        return days == 1 ? "há 1 dia" : $"há {days} dias";
    }

    public static string Inv(string? grupo = null, string? gestao = null, string? estado = null, string? pendencia = null, string? funil = null, string? propriedade = null, string? q = null)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        Add("grupo", grupo); Add("gestao", gestao); Add("estado", estado); Add("pendencia", pendencia); Add("funil", funil); Add("propriedade", propriedade); Add("q", q);
        return parts.Count == 0 ? "/inventario" : "/inventario?" + string.Join("&", parts);
    }

    public static string Pct(double? value) => value is null ? "—" : value.Value.ToString("0.#", Br) + "%";

    public static string Number(int value) => value.ToString("N0", Br);

    public static string ScoreClass(double? score) => score is null ? "" : score >= 85 ? "ok" : score >= 65 ? "warn" : "bad";

    public static string PriorityLabel(Priority p) => p switch { Priority.Critical => "Crítica", Priority.High => "Alta", Priority.Medium => "Média", _ => "Baixa" };

    public static string PriorityCss(Priority p) => p switch { Priority.Critical or Priority.High => "b", Priority.Medium => "w", _ => "m" };

    public static string StateCss(string state) => state switch { States.Healthy => "ok", States.Attention => "w", States.Risk => "b", _ => "m" };

    public static string StateSeg(string state) => state switch { States.Healthy => "s-ok", States.Attention => "s-warn", States.Risk => "s-bad", _ => "c-mute" };

    public static string ManagementSeg(string mode) => mode switch
    {
        Management.CoManaged => "c-both",
        Management.OnlySccm => "c-sccm",
        Management.OnlyIntune or Management.OnlyMam => "c-intune",
        _ => "c-none",
    };

    public static string OwnershipLabel(string ownership, string group) => group == Groups.External ? "Externo" : ownership switch
    {
        "Corporate" => "Corporativo",
        "Personal" => "BYOD",
        _ => "Desconhecida",
    };

    public static string Slug(string group) => group switch
    {
        Groups.Computers => "computadores",
        Groups.Servers => "servidores",
        Groups.CorpMobile => "celulares",
        Groups.ByodMobile or Groups.ByodComputers => "byod",
        Groups.External => "externos",
        _ => group,
    };

    public static string JobLabel(string name) => name switch
    {
        "sccm.devices" => "SCCM",
        "ad.computers" => "Active Directory",
        "intune.devices" => "Intune",
        "entra.devices" => "Entra ID",
        "inventory.reconcile" => "Reconciliação",
        _ => name,
    };

    public static string ReviewKind(string kind) => kind switch
    {
        "DuplicateSerial" => "Serial duplicado",
        "SerialHardwareMismatch" => "Serial igual, fabricante diferente",
        "CloneSuspect" => "Possível clone de máquina virtual",
        "AmbiguousName" => "Nome ambíguo no AD",
        "MultipleIntuneRecords" => "Reenrollment ou registro antigo no Intune",
        _ => kind,
    };
}
