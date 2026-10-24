[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-(alpha|beta)\.\d+)?$')][string]$Version,
    [string]$ReleaseNotes,
    [string]$OutputDirectory = 'artifacts/releases'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseNotes.ps1')
Push-Location $projectRoot
try {
    if (!$ReleaseNotes) { $ReleaseNotes = "releases/$Version.md" }
    $notesPath = (Resolve-Path -LiteralPath $ReleaseNotes).Path
    $outputPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }))
    $publishPath = Join-Path $projectRoot ".artifacts/release-build/$Version-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $publishPath, $outputPath | Out-Null
    # Each version gets a clean output folder, so files of an earlier release never end up in this one.
    Get-ChildItem -LiteralPath $outputPath -File | Remove-Item
    # brim-notify for Windows shares the application's self-contained runtime. It is published first, so that the application's
    # own publish (precompiled, ReadyToRun) writes the shared files last.
    dotnet publish src/BrimDeck.Notify/BrimDeck.Notify.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'brim-notify publish failed.' }
    dotnet publish src/BrimDeck/BrimDeck.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    # The WSL agent: one trimmed Linux file per processor and C library, copied into a distribution only when the user switches
    # it on for dynamic notices. BrimDeck picks wsl\<rid>\brimdeck-agent by what the distribution runs (WslPlatform.Rids).
    foreach ($rid in 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64') {
        $agentPath = Join-Path $publishPath "wsl\$rid"
        dotnet publish src/BrimDeck.WslAgent/BrimDeck.WslAgent.csproj -c Release -r $rid -p:Version=$Version -o $agentPath
        if ($LASTEXITCODE -ne 0) { throw "WSL agent publish failed ($rid)." }
        # Only the agent itself is shipped; the protocol library's symbols are left out.
        Get-ChildItem -LiteralPath $agentPath -File | Where-Object Name -ne 'brimdeck-agent' | Remove-Item
    }
    # The optional identity package for dynamic notices' event mode, registered by the user with the script beside it.
    Copy-Item -Recurse -Force installer/identity (Join-Path $publishPath 'identity')
    $iscc = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1')
    & $iscc -qp "-dAppVersion=$Version" "-dSourceDir=$publishPath" "-o$outputPath" installer/BrimDeck.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Get-Item -LiteralPath (Join-Path $outputPath "BrimDeck-$Version-Setup.exe")
    # The app reads update.json from the latest release and verifies the installer against it before running it.
    $manifest = [ordered]@{
        version = $Version
        notes = Read-ReleaseNotes $notesPath
        file = $installer.Name
        size = $installer.Length
        sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [IO.File]::WriteAllText((Join-Path $outputPath 'update.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Get-ChildItem -LiteralPath $outputPath -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding utf8
    Write-Output "Release artifacts: $outputPath"
    Write-Output "Published application for validation: $publishPath"
}
finally { Pop-Location }
