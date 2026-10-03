namespace Nexus.Collectors.Sccm;

/// <summary>Views read by the Nexus (SPEC §6.1). Grants and access checks are generated from this list.</summary>
public static class SccmViews
{
    public static readonly IReadOnlyList<string> All =
    [
        "v_R_System",
        "v_CH_ClientSummary",
        "v_CH_EvalResults",
        "v_GS_COMPUTER_SYSTEM",
        "v_GS_PC_BIOS",
        "v_GS_SYSTEM_ENCLOSURE",
        "v_GS_COMPUTER_SYSTEM_PRODUCT",
        "v_GS_OPERATING_SYSTEM",
        "v_FullCollectionMembership",
        "v_R_User",
        "v_UsersPrimaryMachines",
        "v_ClientCoManagementState",
        "v_UpdateComplianceStatus",
    ];

    public const string ReaderRole = "azul_nexus_reader";
}
