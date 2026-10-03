# Todo .ps1 de deploy\ precisa: (1) começar com BOM UTF-8, senão o Windows PowerShell 5.1 lê como ANSI e corrompe
# os acentos; (2) ter sintaxe válida no PowerShell que está rodando (no CI: 5.1 e 7).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = 0
foreach ($file in Get-ChildItem $root -Recurse -Filter *.ps1) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $errors = $null; $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    $ok = $hasBom -and (-not $errors -or $errors.Count -eq 0)
    if ($ok) { Write-Host "OK   $($file.Name)" } else {
        $failures++
        Write-Host "FALHA $($file.Name): BOM=$hasBom erros=$(@($errors).Count)" -ForegroundColor Red
        foreach ($e in @($errors)) { Write-Host "   linha $($e.Extent.StartLineNumber): $($e.Message)" }
    }
}
if ($failures -gt 0) { exit 1 }
Write-Host 'Sintaxe e codificação OK.'
