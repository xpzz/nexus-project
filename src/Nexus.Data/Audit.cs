using Nexus.Data.Entities;

namespace Nexus.Data;

public static class Audit
{
    public static void Record(NexusDbContext db, string actor, string action, string? detail = null) =>
        db.AuditEvents.Add(new AuditEvent { At = DateTimeOffset.UtcNow, Actor = actor, Action = action, Detail = detail });
}
