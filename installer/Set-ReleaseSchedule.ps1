param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][ValidateSet('immediate','scheduled')][string]$Mode,
    [string]$Date,
    [string]$PreviousTag,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if (-not $manifest.Version -or -not $manifest.Packages) { throw 'Invalid mod manifest' }
$manifest | Add-Member -Force NoteProperty ActivationMode $Mode
if ($Mode -eq 'scheduled') {
    $day = [datetime]::ParseExact($Date,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture)
    if ($PreviousTag -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $PreviousTag -eq $manifest.Version) { throw 'Specify a different previous release tag' }
    $manifest.Schema = 2
    $manifest | Add-Member -Force NoteProperty ActivateAt ($day.ToString('yyyy-MM-dd')+'T04:00:00+03:00')
    $manifest | Add-Member -Force NoteProperty PreviousManifestUrl ("https://github.com/gyglia/jago-releases/releases/download/$PreviousTag/manifest.json")
} else {
    if ($Date -or $PreviousTag) { throw 'Immediate mode does not take a date or previous tag' }
    $manifest.Schema = 1
    $manifest.PSObject.Properties.Remove('ActivateAt')
    $manifest.PSObject.Properties.Remove('PreviousManifestUrl')
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($manifest | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Output "Prepared $Mode manifest: $OutputPath"
