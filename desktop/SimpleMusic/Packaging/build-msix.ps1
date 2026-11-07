# Builds SimpleMusic as MSIX packages for the Microsoft Store.
#
#   .\build-msix.ps1                       # test build with placeholder identity
#   .\build-msix.ps1 -IdentityName "12345YourName.SimpleMusic" `
#                    -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" `
#                    -PublisherDisplayName "Your Name"
#
# The three identity values come from Partner Center (Product identity page)
# once the app name is reserved. Store uploads don't need signing: the Store
# signs the package. -SignCert is only for installing a test build locally.
param(
    [string]$IdentityName = 'SimpleMusic.LocalTest',
    [string]$Publisher = 'CN=SimpleMusic Local Test',
    [string]$PublisherDisplayName = 'SimpleMusic',
    [string]$Version = '1.0.0.0',
    [string[]]$Architectures = @('x64', 'arm64'),
    [string]$Out = (Join-Path $PSScriptRoot '..\..\dist'),
    [string]$SignCert,
    [string]$SignPassword
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Version must look like 1.2.3.0 (the Store needs the last number to be 0)." }

$sdkBin = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\makeappx.exe' |
    Sort-Object FullName -Descending | Select-Object -First 1 | ForEach-Object DirectoryName
if (-not $sdkBin) { throw 'Windows SDK not found (install it from the Visual Studio Installer).' }
$makeappx = Join-Path $sdkBin 'makeappx.exe'
$makepri = Join-Path $sdkBin 'makepri.exe'
$signtool = Join-Path $sdkBin 'signtool.exe'

$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Out = [System.IO.Path]::GetFullPath($Out)
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$msixDir = Join-Path $Out 'msix'
New-Item -ItemType Directory -Force $msixDir | Out-Null

# Runs a build tool quietly; its output is only shown when it fails.
function Invoke-Tool([string]$exe, [string[]]$toolArgs) {
    $log = & $exe @toolArgs 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host $log
        throw "$(Split-Path $exe -Leaf) failed (exit code $LASTEXITCODE)."
    }
}

$priConfig = Join-Path $Out 'priconfig.xml'
Invoke-Tool $makepri @('createconfig', '/cf', $priConfig, '/dq', 'en-US', '/pv', '10.0.0', '/o')

$template = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw
$packages = @()
foreach ($arch in $Architectures) {
    Write-Host "== $arch ==" -ForegroundColor Cyan
    $layout = Join-Path $Out "layout-$arch"

    # Self-contained: the Store version brings its own .NET, nothing to install first.
    Invoke-Tool 'dotnet' @('publish', $project, '-c', 'Release', '-r', "win-$arch", '--self-contained', 'true',
        '-p:PublishReadyToRun=true', "-p:Version=$($Version -replace '\.0$', '')", '-o', $layout, '--nologo', '-v', 'q')

    Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $layout 'Assets') -Recurse
    $manifest = $template.Replace('$IDENTITY_NAME$', $IdentityName).Replace('$PUBLISHER$', $Publisher).
        Replace('$PUBLISHER_DISPLAY_NAME$', $PublisherDisplayName).Replace('$VERSION$', $Version).Replace('$ARCH$', $arch)
    [System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))

    # resources.pri lets Windows pick the right icon size for each screen.
    Invoke-Tool $makepri @('new', '/pr', $layout, '/cf', $priConfig, '/mn', (Join-Path $layout 'AppxManifest.xml'),
        '/of', (Join-Path $layout 'resources.pri'), '/o')

    $msix = Join-Path $msixDir "SimpleMusic_${Version}_$arch.msix"
    Invoke-Tool $makeappx @('pack', '/d', $layout, '/p', $msix, '/o')
    $packages += $msix
}

$result = $packages
if ($packages.Count -gt 1) {
    $bundle = Join-Path $Out "SimpleMusic_$Version.msixbundle"
    Invoke-Tool $makeappx @('bundle', '/d', $msixDir, '/p', $bundle, '/bv', $Version, '/o')
    $result = @($bundle)
}

if ($SignCert) {
    foreach ($file in $result) {
        Invoke-Tool $signtool @('sign', '/fd', 'SHA256', '/f', $SignCert, '/p', $SignPassword, $file)
    }
}

Write-Host ''
Write-Host 'Done. Upload to Partner Center:' -ForegroundColor Green
$result | ForEach-Object { Write-Host "  $_  ($([Math]::Round((Get-Item $_).Length / 1MB, 1)) MB)" }
