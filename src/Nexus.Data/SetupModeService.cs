using Microsoft.EntityFrameworkCore;
using Nexus.Core.Setup;
using Nexus.Data.Entities;

namespace Nexus.Data;

/// <summary>Setup mode lifecycle (SPEC §4.1): one-time code, expiry, end on SSO, recovery with audit.</summary>
public static class SetupModeService
{
    public static async Task<SetupState> GetAsync(NexusDbContext db, CancellationToken cancellationToken) =>
        await db.Setup.FirstOrDefaultAsync(s => s.Id == 1, cancellationToken) ?? new SetupState();

    /// <summary>Issues a new code, invalidating the previous one. Returns the clear code (shown once).</summary>
    public static async Task<string> IssueCodeAsync(NexusDbContext db, string actor, TimeProvider clock, CancellationToken cancellationToken)
    {
        var state = await EnsureRowAsync(db, cancellationToken);
        var code = SetupCode.Generate();
        state.CodeHash = SetupCode.Hash(code);
        state.CodeExpiresAt = clock.GetUtcNow() + SetupCode.Lifetime;
        state.CodeUsedAt = null;
        Audit.Record(db, actor, "setup.code.issued");
        await db.SaveChangesAsync(cancellationToken);
        return code;
    }

    /// <summary>Validates and consumes the code. A code works once and only while setup mode is active.</summary>
    public static async Task<bool> RedeemCodeAsync(NexusDbContext db, string candidate, string remoteAddress, TimeProvider clock, CancellationToken cancellationToken)
    {
        var state = await EnsureRowAsync(db, cancellationToken);
        var now = clock.GetUtcNow();
        var valid = state.SetupModeActive
            && state.CodeHash is not null
            && state.CodeUsedAt is null
            && state.CodeExpiresAt > now
            && SetupCode.Matches(candidate, state.CodeHash);

        if (valid)
        {
            state.CodeUsedAt = now;
        }

        Audit.Record(db, remoteAddress, valid ? "setup.code.redeemed" : "setup.code.rejected");
        await db.SaveChangesAsync(cancellationToken);
        return valid;
    }

    /// <summary>Called once SSO is validated and at least one user holds Nexus.AdminIntegracao.</summary>
    public static async Task EndAsync(NexusDbContext db, string actor, CancellationToken cancellationToken)
    {
        var state = await EnsureRowAsync(db, cancellationToken);
        state.SetupModeActive = false;
        state.CodeHash = null;
        state.CodeExpiresAt = null;
        Audit.Record(db, actor, "setup.mode.ended");
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>'nexusctl recover-access': reopens setup mode and issues a new code.</summary>
    public static async Task<string> RecoverAccessAsync(NexusDbContext db, string actor, TimeProvider clock, CancellationToken cancellationToken)
    {
        var state = await EnsureRowAsync(db, cancellationToken);
        state.SetupModeActive = true;
        Audit.Record(db, actor, "setup.mode.recovered", "Modo de configuração reaberto por recover-access.");
        await db.SaveChangesAsync(cancellationToken);
        return await IssueCodeAsync(db, actor, clock, cancellationToken);
    }

    private static async Task<SetupState> EnsureRowAsync(NexusDbContext db, CancellationToken cancellationToken)
    {
        var state = await db.Setup.FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);
        if (state is null)
        {
            state = new SetupState { Id = 1, SetupModeActive = true };
            db.Setup.Add(state);
        }

        return state;
    }
}
