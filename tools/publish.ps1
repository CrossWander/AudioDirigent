<#
    Собирает готовый к запуску exe в папку release\.

    По умолчанию — автономная сборка: один файл, рантайм внутри, .NET ставить не нужно (~72 МБ).
    -FrameworkDependent — вариант на ~300 КБ, но требует установленного
    .NET Desktop Runtime 10 (https://dotnet.microsoft.com/download/dotnet/10.0).
#>
param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'release'

# Дожидаемся выхода: без этого exe ещё занят и удалить его не выйдет.
Get-Process AudioDirigent -ErrorAction SilentlyContinue |
    Stop-Process -Force -PassThru |
    Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

# Настройки лежат рядом с exe и пересборку переживают. Особенно config.json: в нём
# записаны порты, которым программа запретила отключать питание, и без него вернуть
# им настройки Windows уже нечем.
$keep = 'config.json', 'log.txt'
if (Test-Path $output) {
    $stale = { Get-ChildItem $output -Force | Where-Object { $_.Name -notin $keep } }

    # Exe ещё секунду занят и после выхода процесса: одиночный файл распаковывает себя сам,
    # а антивирус успевает открыть его на чтение. Ждём и пробуем снова, иначе publish упадёт.
    for ($try = 1; $try -le 10 -and (& $stale); $try++) {
        & $stale | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

        if (& $stale) {
            Start-Sleep -Milliseconds 500
        }
    }

    if (& $stale) {
        throw "не удалось очистить $output - файл занят другим процессом"
    }
}

$arguments = @(
    'publish', (Join-Path $root 'AudioDirigent.csproj')
    '-c', 'Release'
    '-r', 'win-x64'
    '-o', $output
    '--nologo'
    '-p:PublishSingleFile=true'
    '-p:DebugType=none'
)

if ($FrameworkDependent) {
    $arguments += '--self-contained', 'false'
} else {
    $arguments += '--self-contained', 'true'
    $arguments += '-p:EnableCompressionInSingleFile=true'
    $arguments += '-p:IncludeNativeLibrariesForSelfExtract=true'
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с кодом $LASTEXITCODE"
}

$exe = Get-Item (Join-Path $output 'AudioDirigent.exe')
Write-Host ''
Write-Host "$($exe.FullName) — $([Math]::Round($exe.Length / 1MB, 1)) МБ"
