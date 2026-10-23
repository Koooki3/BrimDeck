<#
.SYNOPSIS
Gives the installed BrimDeck a package identity, so that dynamic notices receive Windows' notification change events instead
of reading the notification list on a timer. Optional: without it everything works in polling mode.

.DESCRIPTION
Registers the identity package (AppxManifest.xml next to this script) for the current user, with BrimDeck's install folder as
its external location. Nothing is copied or moved and no administrator rights are needed. Windows only registers an unsigned
package like this one while Developer Mode is on (Settings > System > For developers); a release signed with a trusted
certificate does not need it. Quit BrimDeck first and start it again afterwards.

Run with -Remove to unregister. Uninstalling BrimDeck removes the registration for the user who uninstalls it; upgrading keeps it.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\BrimDeck\identity\Register-NoticeIdentity.ps1"
#>
[CmdletBinding()]
param(
    # The folder that holds BrimDeck.exe; by default the folder above this script.
    [string]$InstallDir = (Split-Path $PSScriptRoot -Parent),
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$name = 'BrimDeck.Notices'

$existing = Get-AppxPackage -Name $name -ErrorAction SilentlyContinue
if ($Remove) {
    if ($existing) { $existing | Remove-AppxPackage; "BrimDeck's identity package was removed. Restart BrimDeck; it reads notifications by polling again." }
    else { "BrimDeck's identity package is not registered." }
    return
}

$exe = Join-Path $InstallDir 'BrimDeck.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "BrimDeck.exe was not found in $InstallDir. Pass -InstallDir with BrimDeck's folder." }
$unlock = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
if ($unlock.AllowDevelopmentWithoutDevLicense -ne 1) {
    throw 'Developer Mode is off. Windows registers this unsigned identity package only in Developer Mode (Settings > System > For developers). Developer Mode lets any unsigned app package be installed on this PC, so turn it on only if you accept that. BrimDeck works without it, in polling mode.'
}
if (Get-Process BrimDeck -ErrorAction SilentlyContinue) { throw 'Quit BrimDeck from its tray icon first, then run this again.' }
if ($existing) { $existing | Remove-AppxPackage }

# The package's own folder: the manifest and logos made from BrimDeck's icon. It stays in the data folder while registered.
$stage = Join-Path $env:LOCALAPPDATA 'BrimDeck\identity'
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Assets') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Destination $stage -Force
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
foreach ($logo in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
    $bitmap = New-Object System.Drawing.Bitmap $logo[1], $logo[1]
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($icon.ToBitmap(), 0, 0, $logo[1], $logo[1])
    $bitmap.Save((Join-Path $stage "Assets\$($logo[0])"), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}
Add-AppxPackage -Register (Join-Path $stage 'AppxManifest.xml') -ExternalLocation $InstallDir
"BrimDeck now has a package identity. Start BrimDeck: Settings > Dynamic notices shows ""event mode"" once Windows allows it."
