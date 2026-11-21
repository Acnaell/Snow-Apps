<#
.SYNOPSIS
  Snow Bench: mede FPS e frametime de um jogo com o Intel PresentMon, sempre do mesmo jeito,
  para comparar ANTES e DEPOIS das otimizações do Snow.

.EXEMPLO
  # 1) Abra o jogo e vá para a cena de teste. 2) Em outro PowerShell (como Administrador):
  powershell -ExecutionPolicy Bypass -File .\snow-bench.ps1 -Game fortnite -Label antes
  # depois de aplicar a predefinição no Snow:
  powershell -ExecutionPolicy Bypass -File .\snow-bench.ps1 -Game fortnite -Label depois

.NOTAS
  - O PresentMon lê eventos de apresentação de quadros do Windows (ETW). Ele não injeta nada no jogo.
  - Resultados ficam em Documentos\SnowBench (CSV por quadro + resumo).
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('fortnite', 'lol', 'valorant')] [string]$Game,
    [string]$Label = 'antes',
    [int]$Seconds = 90,
    [int]$Delay = 10,
    [string]$PresentMon,
    [string]$SnowCli
)

$ErrorActionPreference = 'Stop'
$processes = @{
    fortnite = 'FortniteClient-Win64-Shipping.exe'
    lol      = 'League of Legends.exe'
    valorant = 'VALORANT-Win64-Shipping.exe'
}
$proc = $processes[$Game]

# ── Administrador (o PresentMon precisa para rastrear o jogo)
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "Abra o PowerShell como Administrador e rode de novo." -ForegroundColor Red; exit 2 }

# ── Localiza o PresentMon (parâmetro, mesma pasta do script ou Downloads)
if (-not $PresentMon) {
    $cands = @(Get-ChildItem -Path $PSScriptRoot, "$env:USERPROFILE\Downloads" -Filter 'PresentMon*.exe' -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -match 'x64|PresentMon\.exe' } | Sort-Object LastWriteTime -Descending)
    if ($cands.Count -gt 0) { $PresentMon = $cands[0].FullName }
}
if (-not $PresentMon -or -not (Test-Path $PresentMon)) {
    Write-Host "PresentMon não encontrado." -ForegroundColor Yellow
    Write-Host "Baixe o 'PresentMon-<versão>-x64.exe' em https://github.com/GameTechDev/PresentMon/releases e salve em Downloads."
    exit 3
}

$out = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'SnowBench'
New-Item -ItemType Directory -Force $out | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base = Join-Path $out "$Game-$Label-$stamp"

# ── Diagnóstico do Snow junto (opcional, se o snow-cli.exe estiver por perto)
if (-not $SnowCli) {
    $SnowCli = @(Get-ChildItem -Path $PSScriptRoot, "$env:USERPROFILE\Downloads" -Filter 'snow-cli.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if ($SnowCli -and (Test-Path $SnowCli)) {
    & $SnowCli diagnose | Out-File -Encoding utf8 "$base-diagnostico.txt"
    & $SnowCli games | Out-File -Encoding utf8 "$base-jogos.txt"
}

# ── Espera o jogo estar aberto
$name = [IO.Path]::GetFileNameWithoutExtension($proc)
Write-Host "Esperando o jogo ($proc)..." -ForegroundColor Cyan
while (-not (Get-Process -Name $name -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }

Write-Host "Jogo encontrado. Volte para o jogo e comece a cena de teste." -ForegroundColor Green
for ($i = $Delay; $i -gt 0; $i--) { Write-Host "  gravação começa em $i s..."; Start-Sleep -Seconds 1 }
[console]::Beep(880, 200)
Write-Host "Gravando $Seconds s... (não feche esta janela)" -ForegroundColor Cyan

& $PresentMon --process_name $proc --output_file "$base.csv" --timed $Seconds --terminate_after_timed --v2_metrics | Out-Null
[console]::Beep(660, 200); [console]::Beep(880, 300)

# ── Resumo rápido (média, 1% e 0,1% low, engasgos)
if (-not (Test-Path "$base.csv")) { Write-Host "Nenhum dado capturado. O jogo estava em primeiro plano renderizando?" -ForegroundColor Red; exit 4 }
$rows = Import-Csv "$base.csv"
$col = @('FrameTime', 'MsBetweenPresents', 'msBetweenPresents') | Where-Object { $rows[0].PSObject.Properties.Name -contains $_ } | Select-Object -First 1
$ft = @($rows | ForEach-Object { $v = 0.0; if ([double]::TryParse($_.$col, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$v) -and $v -gt 0) { $v } }) | Sort-Object
if ($ft.Count -lt 100) { Write-Host "Poucos quadros capturados ($($ft.Count))." -ForegroundColor Yellow; exit 4 }
$avg = ($ft | Measure-Object -Average).Average
$p99 = $ft[[int][math]::Floor($ft.Count * 0.99)]
$p999 = $ft[[int][math]::Floor($ft.Count * 0.999)]
$median = $ft[[int]($ft.Count / 2)]
$stutters = @($ft | Where-Object { $_ -gt 2 * $median }).Count
$summary = [ordered]@{
    Jogo = $Game; Rotulo = $Label; Data = (Get-Date).ToString('s'); Quadros = $ft.Count
    FpsMedio = [math]::Round(1000 / $avg, 1)
    Low1 = [math]::Round(1000 / $p99, 1)
    Low01 = [math]::Round(1000 / $p999, 1)
    FrametimeMedianoMs = [math]::Round($median, 2)
    Engasgos = $stutters
}
$summary | ConvertTo-Json | Out-File -Encoding utf8 "$base-resumo.json"
Write-Host ""
Write-Host ("FPS médio: {0}   1% low: {1}   0,1% low: {2}   engasgos (>2x mediana): {3}" -f $summary.FpsMedio, $summary.Low1, $summary.Low01, $summary.Engasgos) -ForegroundColor Green
Write-Host "Arquivos salvos em: $out"
