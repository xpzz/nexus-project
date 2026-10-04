# Testes do provisionamento do Entra ID (deploy/lib/Azure.ps1) contra um Microsoft Graph SIMULADO em memória.
# Execute: pwsh -File deploy/tests/Azure.Tests.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\Common.ps1')
. (Join-Path $PSScriptRoot '..\lib\Azure.ps1')

$script:Failures = 0
function Assert-That {
    param([string]$Name, [scriptblock]$Condition)
    $ok = $false
    try { $ok = [bool](& $Condition) } catch { Write-Host "  exceção: $($_.Exception.Message)" }
    if ($ok) { Write-Host "OK   $Name" } else { Write-Host "FALHA $Name" -ForegroundColor Red; $script:Failures++ }
}

function New-TestCertificate {
    param([string]$Name)
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest("CN=$Name", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    return $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(365))
}

# ---------------------------------------------------------------- Graph simulado
function Reset-FakeGraph {
    $script:Db = @{ Apps = New-Object System.Collections.ArrayList; Sps = New-Object System.Collections.ArrayList; RoleAssignments = New-Object System.Collections.ArrayList
                    Grants = New-Object System.Collections.ArrayList; UserAssignments = New-Object System.Collections.ArrayList; Writes = 0 }
    $graphRoles = @($script:CollectorPermissions | ForEach-Object { @{ id = ([guid]::NewGuid().ToString()); value = $_.Name; allowedMemberTypes = @('Application') } })
    $scopes = @('openid', 'profile', 'User.Read', 'Mail.Read') | ForEach-Object { @{ id = ([guid]::NewGuid().ToString()); value = $_ } }
    [void]$script:Db.Sps.Add(@{ id = 'graph-sp'; appId = $script:GraphAppId; appRoles = $graphRoles; oauth2PermissionScopes = @($scopes); appRoleAssignmentRequired = $false })
}

function Set-FakeKeys {
    param($Target, $Keys)
    $list = @()
    foreach ($k in @($Keys)) {
        $entry = @{} + $k
        if ($k.key) {
            $der = [Convert]::FromBase64String($k.key)
            $sha1 = [System.Security.Cryptography.SHA1]::Create()
            $entry.customKeyIdentifier = [Convert]::ToBase64String($sha1.ComputeHash($der))
            $entry.keyId = [guid]::NewGuid().ToString()
            $entry.Remove('key')
        }
        $list += $entry
    }
    $Target.keyCredentials = $list
}

function Invoke-GraphRequest {
    param([string]$Method, [string]$Uri, [string]$Token, $Body = $null, [int]$Retries = 6)
    $u = [uri]::UnescapeDataString($Uri)
    if ($Method -ne 'GET') { $script:Db.Writes++ }
    if ($Method -eq 'GET' -and $u -like '/organization*') { return @{ value = @(@{ id = 'tenant-1'; displayName = 'Azul Teste' }) } }
    if ($Method -eq 'GET' -and $u -match "^/servicePrincipals\?\`$filter=appId eq '([^']+)'") {
        return @{ value = @($script:Db.Sps | Where-Object { $_.appId -eq $Matches[1] }) }
    }
    if ($Method -eq 'GET' -and $u -match "^/applications\?\`$filter=displayName eq '(.+?)'&") {
        return @{ value = @($script:Db.Apps | Where-Object { $_.displayName -eq $Matches[1] }) }
    }
    if ($Method -eq 'POST' -and $u -eq '/applications') {
        $app = @{ id = [guid]::NewGuid().ToString(); appId = [guid]::NewGuid().ToString(); displayName = $Body.displayName; signInAudience = $Body.signInAudience
                  requiredResourceAccess = $Body.requiredResourceAccess; appRoles = @($Body.appRoles); web = $Body.web }
        Set-FakeKeys -Target $app -Keys $Body.keyCredentials
        [void]$script:Db.Apps.Add($app); return $app
    }
    if ($Method -eq 'PATCH' -and $u -match '^/applications/(.+)$') {
        $app = $script:Db.Apps | Where-Object { $_.id -eq $Matches[1] }
        foreach ($name in 'requiredResourceAccess', 'appRoles', 'web') { if ($Body.ContainsKey($name)) { $app[$name] = $Body[$name] } }
        if ($Body.ContainsKey('keyCredentials')) { Set-FakeKeys -Target $app -Keys $Body.keyCredentials }
        return $null
    }
    if ($Method -eq 'POST' -and $u -eq '/servicePrincipals') {
        $sp = @{ id = [guid]::NewGuid().ToString(); appId = $Body.appId; appRoles = @(); oauth2PermissionScopes = @(); appRoleAssignmentRequired = $false }
        [void]$script:Db.Sps.Add($sp); return $sp
    }
    if ($Method -eq 'PATCH' -and $u -match '^/servicePrincipals/(.+)$') {
        $sp = $script:Db.Sps | Where-Object { $_.id -eq $Matches[1] }
        foreach ($name in $Body.Keys) { $sp[$name] = $Body[$name] }
        return $null
    }
    if ($Method -eq 'GET' -and $u -match '^/servicePrincipals/(.+)/appRoleAssignments$') {
        return @{ value = @($script:Db.RoleAssignments | Where-Object { $_.principalId -eq $Matches[1] }) }
    }
    if ($Method -eq 'POST' -and $u -match '^/servicePrincipals/(.+)/appRoleAssignedTo$') {
        [void]$script:Db.RoleAssignments.Add(@{ principalId = $Body.principalId; resourceId = $Body.resourceId; appRoleId = $Body.appRoleId }); return $null
    }
    if ($Method -eq 'GET' -and $u -match "^/oauth2PermissionGrants\?\`$filter=clientId eq '([^']+)'") {
        return @{ value = @($script:Db.Grants | Where-Object { $_.clientId -eq $Matches[1] }) }
    }
    if ($Method -eq 'POST' -and $u -eq '/oauth2PermissionGrants') {
        [void]$script:Db.Grants.Add(@{ clientId = $Body.clientId; consentType = $Body.consentType; resourceId = $Body.resourceId; scope = $Body.scope }); return $null
    }
    if ($Method -eq 'GET' -and $u -like '/me*') { return @{ id = 'user-me'; userPrincipalName = 'admin@azul.corp' } }
    if ($Method -eq 'GET' -and $u -match '^/users/([^/?]+)\?') {
        $upn = $Matches[1]
        if ($upn -eq 'ana@azul.corp') { return @{ id = 'user-ana'; userPrincipalName = $upn } }
        throw "usuário desconhecido: $upn"
    }
    if ($Method -eq 'GET' -and $u -match '^/users/(.+)/appRoleAssignments$') {
        return @{ value = @($script:Db.UserAssignments | Where-Object { $_.principalId -eq $Matches[1] }) }
    }
    if ($Method -eq 'POST' -and $u -match '^/users/(.+)/appRoleAssignments$') {
        [void]$script:Db.UserAssignments.Add(@{ principalId = $Body.principalId; resourceId = $Body.resourceId; appRoleId = $Body.appRoleId }); return $null
    }
    throw "Rota não simulada: $Method $u"
}

function Write-Log { param([string]$Message, [string]$Level = 'INFO') }   # silencia o log nos testes

# ---------------------------------------------------------------- Utilitários puros
$a = New-DeterministicGuid 'role:Nexus.AdminIntegracao'
Assert-That 'GUID determinístico é estável e distinto por função' { $a -eq (New-DeterministicGuid 'role:Nexus.AdminIntegracao') -and $a -ne (New-DeterministicGuid 'role:Nexus.Leitura') }

$certA = New-TestCertificate 'AzulNexus-Coletor'
$assertion = New-ClientAssertion -Certificate $certA -TenantId 'tenant-1' -ClientId 'client-1'
$parts = $assertion.Split('.')
$header = [Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $parts[0])) | ConvertFrom-Json
$claims = [Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $parts[1])) | ConvertFrom-Json
$rsaPublic = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certA)
$signatureOk = $rsaPublic.VerifyData([Text.Encoding]::UTF8.GetBytes("$($parts[0]).$($parts[1])"), (ConvertFrom-Base64Url $parts[2]), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
Assert-That 'Asserção do cliente: assinatura RS256 confere com a chave pública do certificado' { $signatureOk }
Assert-That 'Asserção do cliente: x5t é a impressão digital e as claims apontam o endpoint e o cliente' {
    $header.alg -eq 'RS256' -and $header.x5t -eq (ConvertTo-Base64Url (ConvertFrom-HexString $certA.Thumbprint)) -and
    $claims.iss -eq 'client-1' -and $claims.sub -eq 'client-1' -and $claims.aud -eq 'https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token' -and $claims.exp -gt $claims.nbf }

$allNames = @($script:CollectorPermissions | ForEach-Object { $_.Name })
Assert-That 'Lista de permissões: 7 obrigatórias + 3 opcionais (SPEC 4.5; Acesso Condicional e sign-ins são opcionais)' { $allNames.Count -eq 10 -and @($script:CollectorPermissions | Where-Object Optional).Count -eq 3 }
foreach ($removed in @($script:CollectorPermissions | Where-Object { -not $_.Optional } | ForEach-Object { $_.Name })) {
    $granted = @($allNames | Where-Object { $_ -ne $removed })
    Assert-That "Validação aponta exatamente a permissão removida: $removed" { (@(Get-MissingPermissions -GrantedRoles $granted)) -join ',' -eq $removed }
}
Assert-That 'Organization.Read.All só é exigida quando pedida' { @(Get-MissingPermissions -GrantedRoles ($allNames | Where-Object { $_ -ne 'Organization.Read.All' })).Count -eq 0 -and (@(Get-MissingPermissions -GrantedRoles ($allNames | Where-Object { $_ -ne 'Organization.Read.All' }) -IncludeOptional) -contains 'Organization.Read.All') }

$er = New-Object System.Management.Automation.ErrorRecord ((New-Object Exception 'x'), 'id', 'NotSpecified', $null)
$er.ErrorDetails = New-Object System.Management.Automation.ErrorDetails('{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}')
Assert-That 'Erro do Graph é traduzido em "código: mensagem"' { (Get-GraphErrorDetail $er) -eq 'Authorization_RequestDenied: Insufficient privileges' }
$er2 = New-Object System.Management.Automation.ErrorRecord ((New-Object Exception 'x'), 'id', 'NotSpecified', $null)
$er2.ErrorDetails = New-Object System.Management.Automation.ErrorDetails('{"error":"invalid_client","error_description":"AADSTS700027: certificado nao reconhecido"}')
Assert-That 'Erro do login (AADSTS) é traduzido' { (Get-GraphErrorDetail $er2) -like 'invalid_client: AADSTS700027*' }

# ---------------------------------------------------------------- Provisionamento (Graph simulado)
$collectorCert = New-TestCertificate 'AzulNexus-Coletor'
$webCert = New-TestCertificate 'AzulNexus-Web'
Reset-FakeGraph
$r1 = Initialize-NexusAzure -Token 't' -PublicUrl 'https://nexus.azul.corp:8443/' -CollectorCertificate $collectorCert -WebCertificate $webCert

Assert-That 'Cria os dois registros e os dois service principals' { $script:Db.Apps.Count -eq 2 -and @($script:Db.Sps | Where-Object { $_.appId -ne $script:GraphAppId }).Count -eq 2 }
Assert-That 'Locatário vem do diretório' { $r1.TenantId -eq 'tenant-1' -and $r1.TenantName -eq 'Azul Teste' }
$collectorApp = $script:Db.Apps | Where-Object { $_.displayName -eq $script:CollectorAppName }
$webApp = $script:Db.Apps | Where-Object { $_.displayName -eq $script:WebAppName }
Assert-That 'Coletor: só permissões de aplicativo (Role), as 10 (7 obrigatórias e 3 opcionais), somente leitura' {
    $access = @($collectorApp.requiredResourceAccess[0].resourceAccess)
    $access.Count -eq 10 -and @($access | Where-Object { $_.type -ne 'Role' }).Count -eq 0 -and $collectorApp.signInAudience -eq 'AzureADMyOrg' }
Assert-That 'Coletor: todas as permissões vêm de ".Read.All" (nenhuma de escrita)' { @($script:CollectorPermissions | Where-Object { $_.Name -notlike '*.Read.All' }).Count -eq 0 }
Assert-That 'Coletor: consentimento do administrador concedido às 10 permissões' { @($script:Db.RoleAssignments | Where-Object { $_.principalId -eq $r1.Collector.ServicePrincipalId -and $_.resourceId -eq 'graph-sp' }).Count -eq 10 }
Assert-That 'Certificados: um por registro, sem segredo de cliente' { @($collectorApp.keyCredentials).Count -eq 1 -and @($webApp.keyCredentials).Count -eq 1 -and -not $collectorApp.ContainsKey('passwordCredentials') -and -not $webApp.ContainsKey('passwordCredentials') }
Assert-That 'Web: 4 funções do SPEC com ids estáveis' {
    $values = @($webApp.appRoles | ForEach-Object { $_.value }) | Sort-Object
    ($values -join ',') -eq 'Nexus.AdminIntegracao,Nexus.Analista,Nexus.Auditoria,Nexus.Leitura' -and ($webApp.appRoles | Where-Object { $_.value -eq 'Nexus.AdminIntegracao' }).id -eq $a.ToString() -and @($webApp.appRoles | Where-Object { $_.allowedMemberTypes -notcontains 'User' }).Count -eq 0 }
Assert-That 'Web: URIs de redirecionamento e logout (SPEC 4.6) sem barra dupla' { $webApp.web.redirectUris -contains 'https://nexus.azul.corp:8443/signin-oidc' -and $webApp.web.logoutUrl -eq 'https://nexus.azul.corp:8443/signout-oidc' -and -not $webApp.web.implicitGrantSettings.enableIdTokenIssuance }
Assert-That 'Web: permissões delegadas openid, profile e User.Read (e só elas)' { @($webApp.requiredResourceAccess[0].resourceAccess).Count -eq 3 -and @($webApp.requiredResourceAccess[0].resourceAccess | Where-Object { $_.type -ne 'Scope' }).Count -eq 0 }
Assert-That 'Web: atribuição obrigatória ativada e consentimento delegado concedido' {
    $webSp = $script:Db.Sps | Where-Object { $_.id -eq $r1.Web.ServicePrincipalId }
    $webSp.appRoleAssignmentRequired -and $script:Db.Grants.Count -eq 1 -and $script:Db.Grants[0].scope -eq 'openid profile User.Read' -and $script:Db.Grants[0].consentType -eq 'AllPrincipals' }
Assert-That 'Primeiro administrador (quem fez o login) recebe Nexus.AdminIntegracao' { $script:Db.UserAssignments.Count -eq 1 -and $script:Db.UserAssignments[0].principalId -eq 'user-me' -and $script:Db.UserAssignments[0].appRoleId -eq $a.ToString() -and $r1.Web.Admin -eq 'admin@azul.corp' }

# Idempotência: rodar de novo não cria nem concede nada
$writesBefore = $script:Db.Writes
$r2 = Initialize-NexusAzure -Token 't' -PublicUrl 'https://nexus.azul.corp:8443' -CollectorCertificate $collectorCert -WebCertificate $webCert
Assert-That 'Segunda execução: mesmos registros, mesmos ids, nada duplicado' {
    $script:Db.Apps.Count -eq 2 -and $r2.Collector.AppId -eq $r1.Collector.AppId -and $r2.Web.AppId -eq $r1.Web.AppId -and
    $script:Db.RoleAssignments.Count -eq 10 -and $script:Db.Grants.Count -eq 1 -and $script:Db.UserAssignments.Count -eq 1 -and
    @($collectorApp.keyCredentials).Count -eq 1 -and @($webApp.keyCredentials).Count -eq 1 -and @($webApp.appRoles).Count -eq 4 }
Assert-That 'Segunda execução só atualiza (PATCH) os dois registros: nenhuma criação' { ($script:Db.Writes - $writesBefore) -eq 2 }

# Rotação de certificado: o novo entra e o antigo continua até ser retirado
$newCert = New-TestCertificate 'AzulNexus-Coletor'
$null = Initialize-NexusAzure -Token 't' -PublicUrl 'https://nexus.azul.corp:8443' -CollectorCertificate $newCert -WebCertificate $webCert
Assert-That 'Certificado novo é acrescentado e o antigo é mantido' { @($collectorApp.keyCredentials).Count -eq 2 }

# Administrador informado e permissão opcional dispensada
Reset-FakeGraph
$r3 = Initialize-NexusAzure -Token 't' -PublicUrl 'https://nexus.azul.corp:8443' -CollectorCertificate $collectorCert -WebCertificate $webCert -AdminUpn 'ana@azul.corp' -SkipOptionalPermissions
Assert-That 'Administrador informado por UPN recebe Nexus.AdminIntegracao' { $script:Db.UserAssignments[0].principalId -eq 'user-ana' -and $r3.Web.Admin -eq 'ana@azul.corp' }
Assert-That '-SkipOptionalPermissions pede 7 permissões (sem Organization.Read.All)' { $script:Db.RoleAssignments.Count -eq 7 }
$threw = $false
Reset-FakeGraph
try { Initialize-NexusAzure -Token 't' -PublicUrl 'https://x' -CollectorCertificate $collectorCert -WebCertificate $webCert -AdminUpn 'inexistente@azul.corp' | Out-Null } catch { $threw = $true }
Assert-That 'Administrador inexistente falha (nada de atribuir ao usuário errado)' { $threw }

# ---------------------------------------------------------------- Validação com a identidade do aplicativo
function New-FakeJwt {
    param([string[]]$Roles)
    $h = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"RS256"}'))
    $p = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes((@{ roles = $Roles; tid = 'tenant-1' } | ConvertTo-Json -Compress)))
    return "$h.$p.assinatura"
}
function Get-AppToken { param($TenantId, $ClientId, $Certificate) return $script:FakeToken }
function Invoke-GraphRequest { param([string]$Method, [string]$Uri, [string]$Token, $Body = $null, [int]$Retries = 6) return @{ value = @() } }

$script:FakeToken = New-FakeJwt -Roles $allNames
$okReport = Test-NexusAzureAccess -TenantId 'tenant-1' -ClientId 'c' -Certificate $collectorCert -IncludeOptional
Assert-That 'Validação completa: token, permissões e 10 chamadas, tudo OK' { @($okReport | Where-Object Status -ne 'OK').Count -eq 0 -and @($okReport | Where-Object { $_.Item -like 'Chamada:*' }).Count -eq 10 }

$script:FakeToken = New-FakeJwt -Roles @($allNames | Where-Object { $_ -notin 'User.Read.All', 'Device.Read.All' })
$badReport = Test-NexusAzureAccess -TenantId 'tenant-1' -ClientId 'c' -Certificate $collectorCert -IncludeOptional
Assert-That 'Validação aponta cada permissão que falta, uma linha por permissão' {
    $fails = @($badReport | Where-Object Status -eq 'Falha' | ForEach-Object { $_.Item })
    $fails.Count -eq 2 -and ($fails -contains 'Permissão User.Read.All') -and ($fails -contains 'Permissão Device.Read.All') }
Assert-That 'Validação não chama a API das permissões que faltam' { @($badReport | Where-Object { $_.Item -eq 'Chamada: User.Read.All' }).Count -eq 0 }

function Get-AppToken { param($TenantId, $ClientId, $Certificate) throw (New-Object System.Management.Automation.RuntimeException 'x') }
# @(): com um único resultado o Windows PowerShell 5.1 devolve o objeto solto (sem Count nem índice).
$errReport = @(Test-NexusAzureAccess -TenantId 'tenant-1' -ClientId 'c' -Certificate $collectorCert)
Assert-That 'Falha ao obter o token vira uma linha de falha explicada' { $errReport.Count -eq 1 -and $errReport[0].Status -eq 'Falha' -and $errReport[0].Item -eq 'Token com o certificado' }

# ---------------------------------------------------------------- azure.json
$result = [pscustomobject]@{ TenantId = 'tenant-1'; TenantName = 'Azul Teste'
    Collector = [pscustomobject]@{ AppId = 'app-c'; ObjectId = 'obj-c'; Thumbprint = 'AA11' }
    Web = [pscustomobject]@{ AppId = 'app-w'; ObjectId = 'obj-w'; Thumbprint = 'BB22'; RedirectUri = 'https://n/signin-oidc'; LogoutUri = 'https://n/signout-oidc' } }
$json = (New-AzureConfig -Result $result -CollectorCertSubject 'CN=AzulNexus-Coletor' -WebCertSubject 'CN=AzulNexus-Web') | ConvertTo-Json -Depth 6
$parsed = $json | ConvertFrom-Json
Assert-That 'azure.json: identificadores, impressões digitais, funções e permissões' {
    $parsed.tenantId -eq 'tenant-1' -and $parsed.collector.clientId -eq 'app-c' -and $parsed.collector.certificateThumbprint -eq 'AA11' -and $parsed.web.clientId -eq 'app-w' -and
    @($parsed.web.roles).Count -eq 4 -and @($parsed.permissions).Count -eq 10 }
Assert-That 'azure.json: nenhum segredo (senha, segredo de cliente, chave privada)' { $json -notmatch '(?i)password|secret|privateKey|clientSecret|BEGIN' }

if ($script:Failures -gt 0) { Write-Host "$script:Failures falha(s)." -ForegroundColor Red; exit 1 }
Write-Host 'Todos os testes do Azure passaram.'
