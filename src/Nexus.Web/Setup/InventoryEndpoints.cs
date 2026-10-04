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

        // Approved exceptions: the gap stays visible, marked as excepted with its reason and an expiry. Needs the analyst role.
        app.MapPost("/mam/excecoes", async (HttpContext http, CurrentAccess access, INexusDbFactory dbFactory, InventorySnapshotService inventory, TimeProvider clock, CancellationToken ct) =>
        {
            if (!access.CanAnalyze)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var form = await http.Request.ReadFormAsync(ct);
            var (id, name, reason) = (form["subjectId"].ToString().Trim(), form["subjectName"].ToString().Trim(), form["reason"].ToString().Trim());
            var control = form["control"].ToString() is "mdm" or "compliance" or "edge" ? form["control"].ToString() : "mam";
            if (id.Length is 0 or > 64 || reason.Length is < 10 or > 1000 || !int.TryParse(form["days"], out var days) || days is < 1 or > 365)
            {
                return Results.BadRequest("Informe o usuário, uma justificativa de pelo menos 10 caracteres e um prazo de 1 a 365 dias.");
            }

            var now = clock.GetUtcNow();
            await using var db = dbFactory.Create();
            db.ProtectionExceptions.Add(new ProtectionException
            {
                SubjectKind = "user", SubjectId = id, SubjectName = name.Length > 256 ? name[..256] : name, Control = control, Reason = reason, ApprovedBy = access.Actor, ApprovedAt = now, ExpiresAt = now.AddDays(days), Active = true,
            });
            Audit.Record(db, access.Actor, "exception.created", $"{control} para {(name.Length > 0 ? name : id)} por {days} dias: {reason}");
            await db.SaveChangesAsync(ct);
            await inventory.RefreshAsync(ct);
            return Results.Redirect("/mam#excecoes");
        });

        app.MapPost("/mam/excecoes/{id:long}/revogar", async (long id, CurrentAccess access, INexusDbFactory dbFactory, InventorySnapshotService inventory, CancellationToken ct) =>
        {
            if (!access.CanAnalyze)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await using var db = dbFactory.Create();
            var exception = await db.ProtectionExceptions.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (exception is null)
            {
                return Results.NotFound();
            }

            exception.Active = false;
            Audit.Record(db, access.Actor, "exception.revoked", $"{exception.Control} para {exception.SubjectName}");
            await db.SaveChangesAsync(ct);
            await inventory.RefreshAsync(ct);
            return Results.Redirect("/mam#excecoes");
        });

        // Manual collection by connector. Needs the integration administrator role; the Worker still honors pauses and CPU limits.
        app.MapPost("/operacao/coletar", async (HttpContext http, CurrentAccess access, INexusDbFactory dbFactory, CancellationToken ct) =>
        {
            if (!access.CanOperate)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var job = (await http.Request.ReadFormAsync(ct))["job"].ToString();
            var allowed = SourceHealthBuilder.Sources.SelectMany(s => s.Jobs).Concat(["inventory.reconcile", "all"]).ToHashSet(StringComparer.Ordinal);
            if (!allowed.Contains(job))
            {
                return Results.BadRequest("Coleta desconhecida.");
            }

            await using var db = dbFactory.Create();
            await CommandQueue.EnqueueAsync(db, CommandTypes.CollectNow, job, access.Actor, ct);
            Audit.Record(db, access.Actor, "collection.requested", job);
            await db.SaveChangesAsync(ct);
            return Results.Redirect("/operacao?message=" + Uri.EscapeDataString($"Coleta de {(job == "all" ? "todas as fontes" : SourceHealthBuilder.JobLabel(job))} solicitada. O Worker a executa assim que puder."));
        });

        app.MapGet("/auditoria/exportar.csv", async (HttpContext http, CurrentAccess access, INexusDbFactory dbFactory, CancellationToken ct) =>
        {
            if (!access.CanAudit)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var (action, who) = (http.Request.Query["acao"].ToString(), http.Request.Query["quem"].ToString());
            await using var db = dbFactory.Create();
            var query = db.AuditEvents.AsNoTracking().AsQueryable();
            if (action.Length > 0) { query = query.Where(e => e.Action.Contains(action)); }
            if (who.Length > 0) { query = query.Where(e => e.Actor.Contains(who)); }
            var events = await query.OrderByDescending(e => e.Id).Take(ExportLimit).ToListAsync(ct);
            Audit.Record(db, access.Actor, "audit.exported", $"{events.Count} eventos");
            await db.SaveChangesAsync(ct);

            var csv = "Quando;Quem;Ação;Detalhe\r\n" + string.Join("\r\n", events.Select(e => string.Join(";", new[] { e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"), e.Actor, e.Action, e.Detail }.Select(InventoryCsv.Cell))));
            return Results.File(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"azulnexus-auditoria-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        });

        app.MapPost("/sair", (EntraStatus entra) => entra.Active
            ? Results.SignOut(new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" }, [EntraSignIn.SessionScheme, EntraSignIn.ChallengeScheme])
            : Results.Redirect("/"));

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
        sb.Append(string.Join(";", Header)).Append("\r\n");
        foreach (var v in rows)
        {
            var a = v.Asset;
            var user = a.Ownership == "Personal" && !includePersonalData ? PersonalData.Mask(a.PrimaryUser) : a.PrimaryUser;
            var sources = string.Join("+", new[] { (a.InSccm, "SCCM"), (a.InIntune, "Intune"), (a.InEntra, "Entra"), (a.InAd, "AD"), (a.InXdr, "XDR"), (a.InNetskope, "Netskope"), (a.HasMam, "MAM") }.Where(x => x.Item1).Select(x => x.Item2));
            sb.Append(string.Join(";", new[]
            {
                a.Id.ToString(), a.Name, AssetTypes.Title(a.AssetType), InventoryQuery.OwnershipOf(v), OperationalStates.Title(a.OperationalState), a.ActivityScore.ToString(CultureInfo.InvariantCulture),
                a.ActivityExplanation, a.Serial, a.Manufacturer, a.Model, a.OperatingSystem, a.OsVersion, user, a.Department, v.Management, sources,
                Date(a.SccmLastSeenAt), Date(a.IntuneLastSyncAt), Date(a.XdrLastSeenAt), Date(a.NetskopeLastSeenAt), Date(a.AdLastLogonAt), Date(a.EntraLastSignInAt),
                a.Confidence switch { "High" => "Alta", "Medium" => "Média", _ => "Baixa" },
            }.Select(Cell))).Append("\r\n");
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
