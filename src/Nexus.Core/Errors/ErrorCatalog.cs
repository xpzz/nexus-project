namespace Nexus.Core.Errors;

public static class ErrorCatalog
{
    public static readonly NexusError ConfigurationMissing = new(
        "NEXUS-CFG-001",
        "A configuração do Nexus não foi encontrada.",
        "Os serviços sobem, mas nenhuma fonte é coletada.",
        "Execute Install-AzulNexus.ps1 novamente ou 'nexusctl configure --from <arquivo.json>'.");

    public static readonly NexusError DatabaseUnreachable = new(
        "NEXUS-DB-001",
        "Não foi possível conectar ao banco do Nexus.",
        "Interface, coletas e histórico ficam indisponíveis.",
        "Confira servidor, banco e se a conta do serviço tem login no SQL. Gere o script do DBA com 'nexusctl db-grant-script'.");

    public static readonly NexusError DatabaseMigrationsPending = new(
        "NEXUS-DB-002",
        "O banco do Nexus está em uma versão anterior à dos binários.",
        "Telas e coletas que dependem das tabelas novas podem falhar.",
        "Execute 'nexusctl migrate' como administrador ou entregue ao DBA o script de 'nexusctl db-script'.");

    public static readonly NexusError SccmNotConfigured = new(
        "NEXUS-SCCM-000",
        "A leitura do SCCM não está configurada.",
        "Indicadores do SCCM aparecem como \"não configurado\"; os demais seguem normais.",
        "Informe servidor SQL e banco do site em Configurações › SCCM ou no arquivo de instalação.");

    public static readonly NexusError SccmSqlUnreachable = new(
        "NEXUS-SCCM-001",
        "Não foi possível conectar ao SQL do site do SCCM.",
        "A coleta do SCCM não roda; o último resultado válido é mantido e marcado como desatualizado.",
        "Confira nome do servidor, porta 1433, firewall e o certificado TLS do SQL. Se o certificado não for confiável, corrija-o ou ative 'Confiar no certificado do servidor' ciente do risco.");

    public static readonly NexusError SccmViewAccessDenied = new(
        "NEXUS-SCCM-002",
        "A conta do serviço Worker não tem permissão de leitura em views do SCCM.",
        "As informações dessas views não são coletadas.",
        "Peça ao DBA para executar o script gerado por 'nexusctl sccm-grant-script'. Ele cria um papel só de leitura e é reversível.");

    public static readonly NexusError SccmLoginFailed = new(
        "NEXUS-SCCM-003",
        "O SQL do site recusou o login da conta do serviço Worker.",
        "A coleta do SCCM não roda.",
        "Crie o login da conta do serviço no SQL do site com o script de 'nexusctl sccm-grant-script'. Em SQL remoto, use gMSA: a conta virtual aparece como a conta de computador.");

    public static readonly NexusError ActiveDirectoryNotConfigured = new(
        "NEXUS-AD-000",
        "A leitura do Active Directory não está configurada.",
        "Equipamentos que só existem no AD não entram no universo conhecido.",
        "Informe o domínio e as bases de busca em Configurações › Active Directory.");

    public static readonly NexusError ActiveDirectoryUnreachable = new(
        "NEXUS-AD-001",
        "Não foi possível consultar o Active Directory.",
        "A coleta do AD não roda; o último resultado válido é mantido e marcado como desatualizado.",
        "Confira se o controlador de domínio responde (porta 389 ou 636), se o servidor está no domínio e se a conta do serviço pode ler os objetos de computador.");

    public static readonly NexusError WorkerNotResponding = new(
        "NEXUS-WRK-001",
        "O serviço AzulNexus.Worker não respondeu ao pedido de verificação.",
        "Coletas e testes de acesso não estão rodando.",
        "Verifique o serviço com 'Get-Service AzulNexus.Worker' e os logs em <dados>\\logs. Para reiniciar: 'Restart-Service AzulNexus.Worker'.");

    public static readonly NexusError CollectionPausedByWindow = new(
        "NEXUS-COL-001",
        "Coleta adiada por janela de pausa configurada.",
        "Os dados seguem com o último resultado válido até o fim da janela.",
        "Nenhuma ação necessária. Para alterar, ajuste as janelas em Configurações › Coleta.");

    public static readonly NexusError CollectionBackoff = new(
        "NEXUS-COL-002",
        "Coleta adiada porque o servidor está sob carga ou as consultas ficaram lentas.",
        "Os dados seguem com o último resultado válido; a coleta será tentada de novo.",
        "Nenhuma ação imediata. Se for frequente, aumente o intervalo ou mova a coleta para fora do horário de pico.");

    public static readonly NexusError SetupCodeInvalid = new(
        "NEXUS-SETUP-001",
        "O código de configuração é inválido, já foi usado ou expirou.",
        "O acesso remoto ao modo de configuração continua bloqueado.",
        "No servidor, como administrador, gere um novo código com 'nexusctl setup-code'.");

    /// <summary>Microsoft Entra error codes translated for the Azure screen (SPEC §4.8).</summary>
    public static readonly IReadOnlyDictionary<string, NexusError> Entra = new Dictionary<string, NexusError>(StringComparer.OrdinalIgnoreCase)
    {
        ["AADSTS700016"] = new("AADSTS700016", "Aplicativo não encontrado neste locatário.", "O Nexus não consegue obter token para o Graph.", "Conferir Client ID e Tenant ID."),
        ["AADSTS700027"] = new("AADSTS700027", "Certificado não reconhecido pelo registro de aplicativo.", "O Nexus não consegue obter token para o Graph.", "Reenviar o .cer e conferir a impressão digital exibida no Nexus."),
        ["AADSTS90002"] = new("AADSTS90002", "Locatário não encontrado.", "Nenhuma chamada ao Entra ou Intune funciona.", "Conferir o Tenant ID."),
        ["AADSTS53003"] = new("AADSTS53003", "Acesso bloqueado por Acesso Condicional.", "O Nexus não consegue obter token.", "Pedir exceção ou política adequada para a identidade do Nexus."),
        ["AADSTS50105"] = new("AADSTS50105", "Usuário sem atribuição no Azul Nexus.", "O usuário não consegue entrar.", "Atribuir o usuário ou grupo a uma função do aplicativo Azul Nexus – Web."),
        ["Graph403"] = new("Graph403", "Permissão ausente ou sem consentimento no Microsoft Graph.", "Os dados que dependem dessa permissão não são coletados.", "Conceder a permissão indicada e o consentimento do administrador, ou regenerar o pacote do caminho B."),
    };

    public static IEnumerable<NexusError> All()
    {
        foreach (var field in typeof(ErrorCatalog).GetFields())
        {
            if (field.GetValue(null) is NexusError error)
            {
                yield return error;
            }
        }

        foreach (var error in Entra.Values)
        {
            yield return error;
        }
    }
}
