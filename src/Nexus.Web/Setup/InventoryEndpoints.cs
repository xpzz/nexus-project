using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexus.Data;
using Nexus.Data.Support;
using Nexus.Data.Entities;
using Nexus.Reconciliation;
using Nexus.Web.Components;

namespace Nexus.Web.Setup;

/// <summary>Saved filters and the controlled CSV export of the inventory. Both check the user's role and write to the audit trail.</summary>
public static class InventoryEndpoints
{
    public const int ExportLimit = 50_000;

    public static void MapInventoryEndpoints(this WebApplication app)
    {
        app.MapPost("/inventario/filtros", async (HttpContext http, CurrentAccess access, INexusDbFactory dbFactory, TimeProvider clock, CancellationToken ct) =>
        {
            if (!access.CanAnalyze)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var form = await http.Request.ReadFormAsync(ct);
            var name = form["name"].ToString().Trim();
            var query = InvQuery.From(form["query"].ToString()).QueryString; // only known keys survive
            if (name.Length is 0 or > 120)
            {
                return Results.BadRequest("Informe um nome de até 120 caracteres.");
            }

            await using var db = dbFactory.Create();
            db.SavedViews.Add(new SavedView { Name = name, Query = query, Owner = access.Actor, Shared = form["shared"] == "true", CreatedAt = clock.GetUtcNow() });
            Audit.Record(db, access.Actor, "savedview.created", $"{name} ({(form["shared"] == "true" ? "compartilhado" : "privado")})");
            await db.SaveChangesAsync(ct);
            return Results.Redirect("/inventario?" + query);
        });

        app.MapPost("/inventario/filtros/{id:long}/excluir", async (long id, CurrentAccess access, INexusDbFactory dbFactory, CancellationToken ct) =>
        {
            await using var db = dbFactory.Create();
            var view = await db.SavedViews.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (view is null)
            {
                return Results.NotFound();
            }

            if (!access.CanAnalyze || (view.Owner != access.Actor && !access.CanOperate))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            db.SavedViews.Remove(view);
            Audit.Record(db, access.Actor, "savedview.deleted", view.Name);
            await db.SaveChangesAsync(ct);
            return Results.Redirect("/inventario");
        });

        app.MapGet("/inventario/exportar.csv", async (HttpContext http, CurrentAccess access, InventorySnapshotService inventory, INexusDbFactory dbFactory, CancellationToken ct) =>
        {
            if (!access.CanExport)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var snapshot = await inventory.GetAsync(ct);
            var q = InvQuery.From(http.Request.Query);
            var now = snapshot.LoadedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : snapshot.LoadedAt;
            var filter = q.ToFilter();
            var rows = InventoryQuery.Sort(snapshot.Views.Where(v => InventoryQuery.Matches(v, filter, now)), q.Get("ordem") is { Length: > 0 } o ? o : "indice", q.Descending).Take(ExportLimit + 1).ToList();
            var truncated = rows.Count > ExportLimit;
            if (truncated)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            await using (var db = dbFactory.Create())
            {
                Audit.Record(db, access.Actor, "inventory.exported", $"{rows.Count} linhas{(truncated ? " (limitado a " + ExportLimit + ")" : "")}; filtro: {q.QueryString}");
                await db.SaveChangesAsync(ct);
            }

            var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(InventoryCsv.Build(rows, access.CanSeePersonalData))).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"azulnexus-inventario-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        });
    }
}

public static class InventoryCsv
{
    private static readonly string[] Header =
    [
        "Id Nexus", "Equipamento", "Tipo", "Propriedade", "Estado operacional", "Score de atividade", "Explicação", "Serial", "Fabricante", "Modelo", "Sistema", "Versão",
        "Usuário", "Área", "Gerenciamento", "Fontes", "Último SCCM", "Último Intune", "Último Cortex XDR", "Último Netskope", "Último AD", "Último Entra ID", "Confiança da identidade",
    ];

    public static string Build(IReadOnlyList<AssetView> rows, bool includePersonalData)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", Header));
        foreach (var v in rows)
        {
            var a = v.Asset;
            var user = a.Ownership == "Personal" && !includePersonalData ? PersonalData.Mask(a.PrimaryUser) : a.PrimaryUser;
            var sources = string.Join("+", new[] { (a.InSccm, "SCCM"), (a.InIntune, "Intune"), (a.InEntra, "Entra"), (a.InAd, "AD"), (a.InXdr, "XDR"), (a.InNetskope, "Netskope"), (a.HasMam, "MAM") }.Where(x => x.Item1).Select(x => x.Item2));
            sb.AppendLine(string.Join(";", new[]
            {
                a.Id.ToString(), a.Name, AssetTypes.Title(a.AssetType), InventoryQuery.OwnershipOf(v), OperationalStates.Title(a.OperationalState), a.ActivityScore.ToString(CultureInfo.InvariantCulture),
                a.ActivityExplanation, a.Serial, a.Manufacturer, a.Model, a.OperatingSystem, a.OsVersion, user, a.Department, v.Management, sources,
                Date(a.SccmLastSeenAt), Date(a.IntuneLastSyncAt), Date(a.XdrLastSeenAt), Date(a.NetskopeLastSeenAt), Date(a.AdLastLogonAt), Date(a.EntraLastSignInAt),
                a.Confidence switch { "High" => "Alta", "Medium" => "Média", _ => "Baixa" },
            }.Select(Cell)));
        }

        return sb.ToString();
    }

    private static string Date(DateTimeOffset? at) => at?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Quotes the cell and neutralizes spreadsheet formulas (a value starting with = + - @ would run as one when opened in Excel).</summary>
    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}
