param(
    [string] $InnoCompiler,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repository 'src\GameProfileManager\GameProfileManager.csproj'
$setupScript = Join-Path $repository 'installer\GameProfileManager.iss'
$output = if ($OutputDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) }
          else { Join-Path $repository 'artifacts\installer' }

if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompiler = $command.Source }
}
if (-not $InnoCompiler) {
    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $InnoCompiler = $candidate; break }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Не найден ISCC.exe. Установите Inno Setup 6/7 или передайте -InnoCompiler C:\path\to\ISCC.exe.'
}

$stagingRoot = Join-Path $repository 'artifacts\installer-staging'
$stage = Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage, $output -Force | Out-Null
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:CopyOutputSymbolsToPublishDirectory=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

    foreach ($required in @('GameProfileManager.exe', 'GameProfileManager.Engine.dll', 'GameProfileManager.pri',
                           'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll',
                           'SDL3.dll', 'SDL3-LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) {
            throw "В публикации отсутствует $required"
        }
    }

    & $InnoCompiler "/DSourceDir=$stage" "/O$output" $setupScript
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe завершился с кодом $LASTEXITCODE" }

    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'GameProfileManager.exe')).FileVersion
    $installer = Join-Path $output "GameProfileManager-Setup-$version-win-x64.exe"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "Установщик не найден: $installer"
    }
    Write-Output $installer
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($stagingRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($resolvedStage.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
