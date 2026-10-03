<#
.SYNOPSIS
    Gera o pacote de implantação do Azul Nexus (dotnet publish) em artifacts\AzulNexus-<versão>.

.DESCRIPTION
    Layout do pacote:
      web\     site (self-contained): serviço do Windows com Kestrel (padrão) ou IIS in-process (exige o Hosting Bundle)
      app\     AzulNexus.Worker.exe e nexusctl.exe (self-contained: não exigem runtime instalado)
      deploy\  Install-AzulNexus.ps1, Uninstall-AzulNexus.ps1, lib\, install.sample.json
      docs\    guias

    Roda em qualquer máquina com o .NET 10 SDK (inclusive Linux): o destino é sempre win-x64.

.PARAMETER HostingBundlePath
    Instalador oficial do ASP.NET Core Hosting Bundle (dotnet-hosting-*-win.exe) a incluir em prereq\ do pacote.

.PARAMETER CodeSigningThumbprint
    Se informado (Windows), assina .dll, .exe e .ps1 com o certificado de code signing do repositório da máquina.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [switch]$NoZip,
    [string]$CodeSigningThumbprint,
    [string]$TimestampServer,
    [string]$HostingBundlePath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'artifacts' }

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Versão não encontrada em Directory.Build.props.' }

$package = Join-Path $OutputRoot "AzulNexus-$version"
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Path $package | Out-Null

function Invoke-Publish {
    param([string]$Project, [string]$Output, [bool]$SelfContained)
    Write-Host "dotnet publish $Project -> $Output (self-contained: $SelfContained)"
    $arguments = @(
        'publish', (Join-Path $root "src\$Project\$Project.csproj"),
        '-c', $Configuration, '-r', 'win-x64', '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
        '-o', $Output, '-p:DebugType=none', '-p:DebugSymbols=false', "-p:Version=$version", '--nologo', '-v', 'q'
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou para $Project (código $LASTEXITCODE)." }
}

# Self-contained: o modo "service" não precisa de runtime nem de Hosting Bundle (o modo "iis" ainda precisa do módulo do IIS).
Invoke-Publish -Project 'Nexus.Web' -Output (Join-Path $package 'web') -SelfContained $true
Invoke-Publish -Project 'Nexus.Worker' -Output (Join-Path $package 'app') -SelfContained $true
Invoke-Publish -Project 'Nexus.Cli' -Output (Join-Path $package 'app') -SelfContained $true

$deploy = Join-Path $package 'deploy'
New-Item -ItemType Directory -Path (Join-Path $deploy 'lib') -Force | Out-Null
foreach ($file in 'Install-AzulNexus.ps1', 'Install-AzulNexusAzure.ps1', 'Uninstall-AzulNexus.ps1', 'install.sample.json') {
    Copy-Item (Join-Path $PSScriptRoot $file) $deploy
}
Copy-Item (Join-Path $PSScriptRoot 'lib\*.ps1') (Join-Path $deploy 'lib')
# Atalhos de duplo clique na raiz do pacote (o "instalador" sem .exe, ADR-0001).
foreach ($file in 'Instalar.cmd', 'Verificar-Ambiente.cmd', 'Configurar-Azure.cmd') { Copy-Item (Join-Path $PSScriptRoot $file) $package }
if ($HostingBundlePath) {
    if (-not (Test-Path $HostingBundlePath)) { throw "Hosting Bundle não encontrado em $HostingBundlePath." }
    $prereq = Join-Path $package 'prereq'
    New-Item -ItemType Directory -Path $prereq | Out-Null
    Copy-Item $HostingBundlePath (Join-Path $prereq ([IO.Path]::GetFileName($HostingBundlePath)))
}
$guides = Join-Path $root 'docs\guias'
if (Test-Path $guides) { Copy-Item $guides (Join-Path $package 'docs') -Recurse }
Set-Content -Path (Join-Path $package 'VERSION.txt') -Value $version -Encoding ASCII

# Verificações do pacote: o que o instalador exige tem de estar lá.
$required = @('web\AzulNexus.Web.dll', 'web\web.config', 'app\AzulNexus.Worker.exe', 'app\nexusctl.exe', 'deploy\Install-AzulNexus.ps1', 'deploy\Install-AzulNexusAzure.ps1', 'deploy\lib\Azure.ps1', 'Instalar.cmd', 'Configurar-Azure.cmd')
foreach ($item in $required) {
    if (-not (Test-Path (Join-Path $package $item))) { throw "Pacote incompleto: falta $item." }
}
$webConfig = Get-Content (Join-Path $package 'web\web.config') -Raw
if ($webConfig -notmatch 'hostingModel="inprocess"') { throw 'web.config sem hostingModel="inprocess".' }

if ($CodeSigningThumbprint) {
    if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'A assinatura de código exige Windows.' }
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $CodeSigningThumbprint | Select-Object -First 1
    if (-not $certificate) { throw "Certificado de code signing $CodeSigningThumbprint não encontrado." }
    $files = Get-ChildItem $package -Recurse -Include *.exe, *.ps1 | Where-Object { $_.FullName -notlike '*\app\*' -or $_.Name -like 'AzulNexus*' -or $_.Name -eq 'nexusctl.exe' }
    $files += Get-ChildItem $package -Recurse -Filter 'Nexus.*.dll'
    $files += Get-ChildItem $package -Recurse -Filter 'AzulNexus.*.dll'
    foreach ($file in ($files | Sort-Object FullName -Unique)) {
        $arguments = @{ FilePath = $file.FullName; Certificate = $certificate; HashAlgorithm = 'SHA256' }
        if ($TimestampServer) { $arguments['TimestampServer'] = $TimestampServer }
        $result = Set-AuthenticodeSignature @arguments
        if ($result.Status -ne 'Valid') { throw "Falha ao assinar $($file.Name): $($result.StatusMessage)" }
    }
    Write-Host "Arquivos assinados: $(@($files | Sort-Object FullName -Unique).Count)"
}

$sums = Get-ChildItem $package -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($package.Length + 1).Replace('\', '/')
}
Set-Content -Path (Join-Path $package 'SHA256SUMS.txt') -Value $sums -Encoding ASCII

if (-not $NoZip) {
    $zip = "$package.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip
    Write-Host "Pacote: $zip"
}
Write-Host "Pasta do pacote: $package"
