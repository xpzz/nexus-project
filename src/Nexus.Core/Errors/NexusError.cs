namespace Nexus.Core.Errors;

/// <summary>
/// Every message shown to a person states what happened, the impact and how to fix it (SPEC §2.4 and §13).
/// </summary>
public sealed record NexusError(string Code, string WhatHappened, string Impact, string HowToFix, string? Script = null)
{
    public NexusError WithScript(string script) => this with { Script = script };

    public NexusError WithDetail(string detail) => this with { WhatHappened = $"{WhatHappened} Detalhe: {detail}" };

    public override string ToString() =>
        $"[{Code}] {WhatHappened}{Environment.NewLine}Impacto: {Impact}{Environment.NewLine}Como resolver: {HowToFix}";
}
