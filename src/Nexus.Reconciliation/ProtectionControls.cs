namespace Nexus.Reconciliation;

/// <summary>What the policy says about a control. Setting a control is not the same as it protecting data on devices (see <see cref="EvidenceLevel"/>).</summary>
public enum ControlSetting { Configured, NotConfigured, NotApplicable }

/// <summary>How much proof there is that a control is working, from the weakest to the strongest claim.</summary>
public enum EvidenceLevel { NotApplicable, NotConfigured, ConfiguredNotAssigned, AssignedNoEvidence, InsufficientData, EffectiveProven }

public sealed record ControlResult(string Key, string Label, ControlSetting Setting, string Value);

public static class EvidenceLevels
{
    public static string Title(EvidenceLevel l) => l switch
    {
        EvidenceLevel.NotApplicable => "Não aplicável",
        EvidenceLevel.NotConfigured => "Não configurado",
        EvidenceLevel.ConfiguredNotAssigned => "Configurado, sem atribuição",
        EvidenceLevel.AssignedNoEvidence => "Atribuído, sem evidência de aplicação",
        EvidenceLevel.InsufficientData => "Sem evidência suficiente",
        _ => "Aplicação efetiva comprovada",
    };

    public static string Css(EvidenceLevel l) => l switch
    {
        EvidenceLevel.EffectiveProven => "ok",
        EvidenceLevel.AssignedNoEvidence or EvidenceLevel.InsufficientData => "w",
        EvidenceLevel.ConfiguredNotAssigned => "b",
        _ => "m",
    };

    public static string Icon(EvidenceLevel l) => l switch
    {
        EvidenceLevel.EffectiveProven => "✔",
        EvidenceLevel.AssignedNoEvidence or EvidenceLevel.InsufficientData => "◐",
        EvidenceLevel.ConfiguredNotAssigned => "✕",
        EvidenceLevel.NotConfigured => "○",
        _ => "–",
    };

    /// <summary>
    /// The claim a control can honestly make: configured in the policy, assigned to someone, and applied to devices (registrations whose applied policies include it).
    /// A policy that is configured and assigned but applied to no registration is not "protection": the proof is missing.
    /// </summary>
    public static EvidenceLevel Of(ControlSetting setting, bool assigned, int appliedRegistrations, bool registrationsCollected) => setting switch
    {
        ControlSetting.NotApplicable => EvidenceLevel.NotApplicable,
        ControlSetting.NotConfigured => EvidenceLevel.NotConfigured,
        _ when !assigned => EvidenceLevel.ConfiguredNotAssigned,
        _ when !registrationsCollected => EvidenceLevel.InsufficientData,
        _ when appliedRegistrations > 0 => EvidenceLevel.EffectiveProven,
        _ => EvidenceLevel.AssignedNoEvidence,
    };
}

/// <summary>Reads the app protection policy settings that matter for keeping corporate data inside managed apps.</summary>
public static class ProtectionControls
{
    public static readonly (string Key, string Label)[] All =
    [
        ("clipboard", "Copiar e colar"),
        ("inbound", "Dados de entrada somente de aplicativos gerenciados"),
        ("storage", "Salvar apenas em OneDrive e SharePoint"),
        ("backup", "Backup em armazenamento pessoal bloqueado"),
        ("pin", "PIN ou biometria"),
        ("encryption", "Criptografia dos dados do aplicativo"),
        ("screenshot", "Captura de tela bloqueada"),
        ("outbound", "Transferência para aplicativos não gerenciados bloqueada"),
        ("wipe", "Limpeza seletiva por inatividade"),
    ];

    private static string? S(IReadOnlyDictionary<string, string> s, string key) => s.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private static bool? B(IReadOnlyDictionary<string, string> s, string key) => S(s, key) is { } v && bool.TryParse(v, out var b) ? b : null;

    public static IReadOnlyList<ControlResult> Evaluate(string platform, IReadOnlyDictionary<string, string> settings)
    {
        var android = platform.Equals("Android", StringComparison.OrdinalIgnoreCase);
        var results = new List<ControlResult>();

        var clip = S(settings, "allowedOutboundClipboardSharingLevel");
        results.Add(clip switch
        {
            null => new("clipboard", All[0].Label, ControlSetting.NotConfigured, "sem valor lido do Graph"),
            "allowed" => new("clipboard", All[0].Label, ControlSetting.NotConfigured, "liberado para qualquer aplicativo"),
            "blocked" => new("clipboard", All[0].Label, ControlSetting.Configured, "bloqueado"),
            "managedApps" => new("clipboard", All[0].Label, ControlSetting.Configured, "somente entre aplicativos gerenciados"),
            "managedAppsWithPasteIn" => new("clipboard", All[0].Label, ControlSetting.Configured, "entre gerenciados; colar de qualquer aplicativo"),
            _ => new("clipboard", All[0].Label, ControlSetting.Configured, clip),
        });

        var inbound = S(settings, "allowedInboundDataTransferSources");
        results.Add(inbound is null or "allApps"
            ? new("inbound", All[1].Label, ControlSetting.NotConfigured, inbound is null ? "sem valor lido do Graph" : "aceita dados de qualquer aplicativo")
            : new("inbound", All[1].Label, ControlSetting.Configured, inbound == "none" ? "nenhuma origem aceita" : "somente aplicativos gerenciados"));

        var locations = (S(settings, "allowedDataStorageLocations") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var corporateOnly = locations.Length > 0 && locations.All(l => l is "oneDriveForBusiness" or "sharePoint");
        var saveAsBlocked = B(settings, "saveAsBlocked") == true;
        results.Add(saveAsBlocked || corporateOnly
            ? new("storage", All[2].Label, ControlSetting.Configured, saveAsBlocked ? "salvar como bloqueado" : "somente " + string.Join(" e ", locations))
            : new("storage", All[2].Label, ControlSetting.NotConfigured, locations.Length == 0 ? "qualquer local permitido" : "também permite " + string.Join(", ", locations.Where(l => l is not "oneDriveForBusiness" and not "sharePoint"))));

        results.Add(B(settings, "dataBackupBlocked") == true
            ? new("backup", All[3].Label, ControlSetting.Configured, "bloqueado")
            : new("backup", All[3].Label, ControlSetting.NotConfigured, "backup permitido"));

        var pin = B(settings, "pinRequired") == true;
        var bio = B(settings, "fingerprintBlocked") == true || B(settings, "biometricAuthenticationBlocked") == true ? "biometria bloqueada" : "biometria permitida";
        results.Add(pin
            ? new("pin", All[4].Label, ControlSetting.Configured, $"PIN obrigatório{(S(settings, "minimumPinLength") is { } n ? $" (mínimo {n})" : "")}; {bio}")
            : new("pin", All[4].Label, ControlSetting.NotConfigured, "PIN do aplicativo não exigido"));

        if (android)
        {
            results.Add(B(settings, "encryptAppData") == true
                ? new("encryption", All[5].Label, ControlSetting.Configured, "dados do aplicativo criptografados")
                : new("encryption", All[5].Label, ControlSetting.NotConfigured, "criptografia do aplicativo desligada"));
        }
        else
        {
            var kind = S(settings, "appDataEncryptionType");
            results.Add(kind is null or "useDeviceSettings"
                ? new("encryption", All[5].Label, ControlSetting.NotConfigured, "usa a configuração do dispositivo")
                : new("encryption", All[5].Label, ControlSetting.Configured, kind switch
                {
                    "afterDeviceRestart" => "após reiniciar o dispositivo",
                    "whenDeviceLockedExceptOpenFiles" => "com o dispositivo bloqueado (exceto arquivos abertos)",
                    "whenDeviceLocked" => "com o dispositivo bloqueado",
                    _ => kind,
                }));
        }

        results.Add(android
            ? B(settings, "screenCaptureBlocked") == true
                ? new("screenshot", All[6].Label, ControlSetting.Configured, "captura de tela bloqueada")
                : new("screenshot", All[6].Label, ControlSetting.NotConfigured, "captura de tela permitida")
            : new("screenshot", All[6].Label, ControlSetting.NotApplicable, "a política de MAM do iOS não oferece este controle"));

        var outbound = S(settings, "allowedOutboundDataTransferDestinations");
        results.Add(outbound is null or "allApps"
            ? new("outbound", All[7].Label, ControlSetting.NotConfigured, outbound is null ? "sem valor lido do Graph" : "pode enviar dados a qualquer aplicativo")
            : new("outbound", All[7].Label, ControlSetting.Configured, outbound == "none" ? "nenhum destino permitido" : "somente aplicativos gerenciados"));

        var wipe = S(settings, "periodOfflineBeforeWipeIsEnforced");
        results.Add(wipe is null or "P0D" or "PT0S"
            ? new("wipe", All[8].Label, ControlSetting.NotConfigured, "sem limite offline para limpar os dados")
            : new("wipe", All[8].Label, ControlSetting.Configured, "limpeza após " + Iso(wipe) + " offline"));
        return results;
    }

    private static string Iso(string duration) => System.Xml.XmlConvert.ToTimeSpan(duration) is var t && t.TotalDays >= 1 ? $"{(int)t.TotalDays} dia(s)" : duration;
}

/// <summary>What the Edge URL allow and block lists contain and how close the block list is to its operational limit.</summary>
public sealed record EdgeListInfo(int AllowCount, int BlockCount, int BlockLimit, int WarningAt, string State, IReadOnlyList<string> Keys)
{
    public const string Ok = "ok", Warning = "warning", Full = "full", NoList = "none";

    public int Left => Math.Max(0, BlockLimit - BlockCount);

    public static string StateTitle(string state) => state switch { Warning => "perto do limite", Full => "no limite ou acima", NoList => "sem lista de bloqueio", _ => "com folga" };
}

public static class EdgeUrlLists
{
    /// <summary>Entries of a list value: pipe or semicolon separated, a JSON array, or one per line.</summary>
    public static IReadOnlyList<string> Entries(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var text = value.Trim();
        if (text.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                return doc.RootElement.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (System.Text.Json.JsonException)
            {
                // fall through: treat as plain text
            }
        }

        return text.Split(['|', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsBlockKey(string key) => key.Contains("block", StringComparison.OrdinalIgnoreCase) && key.Contains("url", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowKey(string key) => (key.Contains("allow", StringComparison.OrdinalIgnoreCase) && key.Contains("url", StringComparison.OrdinalIgnoreCase)) || key.Contains("AllowListURLs", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeEdgeSettings(IReadOnlyDictionary<string, string> settings) => settings.Keys.Any(k => IsBlockKey(k) || IsAllowKey(k));

    public static EdgeListInfo Analyze(IReadOnlyDictionary<string, string> settings, int limit, int reservePercent)
    {
        var allow = settings.Where(kv => IsAllowKey(kv.Key)).SelectMany(kv => Entries(kv.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var block = settings.Where(kv => IsBlockKey(kv.Key)).SelectMany(kv => Entries(kv.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var warningAt = (int)Math.Floor(limit * (100 - Math.Clamp(reservePercent, 0, 90)) / 100.0);
        var keys = settings.Keys.Where(k => IsAllowKey(k) || IsBlockKey(k)).Order().ToList();
        var state = !settings.Keys.Any(IsBlockKey) ? EdgeListInfo.NoList : block >= limit ? EdgeListInfo.Full : block >= warningAt ? EdgeListInfo.Warning : EdgeListInfo.Ok;
        return new EdgeListInfo(allow, block, limit, warningAt, state, keys);
    }
}
