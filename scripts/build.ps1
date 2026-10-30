param(
    [switch]$SkipDependencies,
    [switch]$DependenciesOnly,
    [string]$PerlPath,
    [string]$Output = 'publish-timing',
    [string]$Archive = 'NetworkDownloader-Timing-win-x64.zip'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = $sourceRoot
$mappedDrive = $null
$pushed = $false
try {
    function Run([string]$Program, [string[]]$Arguments) {
        & $Program @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Program $($Arguments -join ' ')" }
    }
    # OpenSSL's Windows Perl/nasm/nmake toolchain cannot reliably build in Unicode paths.
    # Map this project, never move it, and release only the mapping created by this invocation.
    if ($sourceRoot -match '[^\x00-\x7F]') {
        foreach ($letter in @('N:', 'O:', 'P:', 'Q:', 'R:', 'S:', 'T:', 'U:', 'V:', 'W:', 'X:', 'Y:', 'Z:')) {
            if (!(Test-Path "$letter\") -and !(Get-PSDrive ($letter.TrimEnd(':')) -ErrorAction SilentlyContinue)) {
                Run subst.exe @($letter, $sourceRoot)
                $mappedDrive = $letter
                $projectRoot = "$letter\"
                break
            }
        }
        if (!$mappedDrive) { throw 'No free drive letter for the native build. Build from an ASCII-only directory.' }
    }
    Push-Location $projectRoot
    $pushed = $true
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'Install Visual Studio C++ desktop build tools first.' }
    $cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (!(Test-Path $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
    $vcpkgRoot = Join-Path $projectRoot 'artifacts\vcpkg'
    if (!$env:VCPKG_MAX_CONCURRENCY) { $env:VCPKG_MAX_CONCURRENCY = '4' }
    $vcpkg = Join-Path $vcpkgRoot 'vcpkg.exe'
    if (!$SkipDependencies) {
        $baseline = '866e9e258a76437074a5cf02d4c2d71b53bff2f8'
        if (!(Test-Path $vcpkgRoot)) {
            Run git @('clone', '--depth', '1', 'https://github.com/microsoft/vcpkg.git', $vcpkgRoot)
            Run git @('-C', $vcpkgRoot, 'fetch', '--depth', '1', 'origin', $baseline)
            Run git @('-C', $vcpkgRoot, 'checkout', '--detach', $baseline)
        }
        $actual = (& git -C $vcpkgRoot rev-parse HEAD).Trim()
        if ($actual -ne $baseline) { throw "Expected vcpkg baseline $baseline. Use a clean checkout of that commit in $vcpkgRoot." }
        if (!(Test-Path $vcpkg)) { Run (Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat') @('-disableMetrics') }
        # The pinned ports require VCPKG_COMMAND, absent in the pinned bootstrap tool's environment.
        $compatPatch = Join-Path $projectRoot 'native\vcpkg-tool-compat.patch'
        $portsText = [IO.File]::ReadAllText((Join-Path $vcpkgRoot 'scripts\ports.cmake'))
        if (!$portsText.Contains('set(ENV{VCPKG_COMMAND}')) { Run git @('-C', $vcpkgRoot, 'apply', '--ignore-whitespace', $compatPatch) }
        if ($PerlPath) {
            if (!(Test-Path -LiteralPath $PerlPath)) { throw "Perl does not exist: $PerlPath" }
            $env:BT_BUILD_PERL = (Resolve-Path -LiteralPath $PerlPath).Path
        }
        Run $vcpkg @('install', '--x-manifest-root=native', '--x-install-root=artifacts/native-deps',
            '--x-buildtrees-root=artifacts/native-trees', '--x-packages-root=artifacts/native-packages',
            '--overlay-triplets=native/triplets', '--triplet=x64-windows-release', '--host-triplet=x64-windows-release')
    }
    if ($DependenciesOnly) { return }
    $installed = Join-Path $projectRoot 'artifacts\native-deps\x64-windows-release'
    Run $cmake @('-S', 'native', '-B', 'artifacts/native-build', '-A', 'x64', "-DCMAKE_PREFIX_PATH=$installed")
    Run $cmake @('--build', 'artifacts/native-build', '--config', 'Release', '--parallel', '4')
    Run dotnet @('build', 'NetworkDownloader.slnx', '-c', 'Release', '--artifacts-path', 'artifacts/bt')
    Run dotnet @('artifacts/bt/bin/NetworkDownloader.Tests/release/NetworkDownloader.Tests.dll')
    Run dotnet @('restore', 'NetworkDownloader.slnx', '--artifacts-path', 'artifacts/bt', '-r', 'win-x64')
    Run dotnet @('publish', 'NetworkDownloader.App', '-c', 'Release', '--no-restore', '--artifacts-path', 'artifacts/bt', '-r', 'win-x64', '--self-contained', 'false', '-o', $Output)
    Run dotnet @('publish', 'NetworkDownloader.BtWorker', '-c', 'Release', '--no-restore', '--artifacts-path', 'artifacts/bt', '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $Output 'bt-worker'))
    Copy-Item -LiteralPath README.md -Destination (Join-Path $Output 'README.md')
    $licenses = Join-Path $Output 'third-party-licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $licenses 'NOTICES.md')
    Get-ChildItem (Join-Path $installed 'share') -Directory | ForEach-Object {
        $copyright = Join-Path $_.FullName 'copyright'
        if (Test-Path -LiteralPath $copyright) { Copy-Item -LiteralPath $copyright -Destination (Join-Path $licenses ($_.Name + '.txt')) }
    }
    $packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $sqliteNotice = Join-Path $packageCache 'sqlite\3.53.4\LICENSE.txt'
    if (!(Test-Path -LiteralPath $sqliteNotice)) { throw 'SQLite native license was not found in the NuGet cache.' }
    Copy-Item -LiteralPath $sqliteNotice -Destination (Join-Path $licenses 'sqlite-native.txt')
    if (Test-Path -LiteralPath $Archive) { throw "Archive already exists: $Archive. Choose a new -Archive path." }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $Archive
    Write-Host "Published: $Output\NetworkDownloader.exe"
} finally {
    if ($pushed) { Pop-Location }
    if ($mappedDrive) { & subst.exe $mappedDrive /D }
}
