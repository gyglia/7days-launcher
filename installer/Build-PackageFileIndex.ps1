param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = @(foreach ($entry in $zip.Entries) {
        if (-not $entry.Name) { continue }
        if ($entry.FullName.Contains('\') -or $entry.FullName.Split('/') -contains '..' -or $entry.FullName.StartsWith('/')) { throw 'Unsafe ZIP path' }
        if (-not $entry.FullName.Contains('/')) { continue } # Root readme is not installed as a mod.
        if (-not $seen.Add($entry.FullName)) { throw 'Duplicate ZIP path' }
        $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $stream.Dispose(); $sha.Dispose() }
        [ordered]@{ Path = $entry.FullName; Bytes = $entry.Length; Sha256 = $digest }
    })
} finally { $zip.Dispose() }
if ($files.Count -eq 0) { throw 'No mod files in package' }
$index = [ordered]@{ PackageSha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash; Files = $files }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($index | ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{ FileIndexSha256 = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash; FileIndexBytes = (Get-Item -LiteralPath $OutputPath).Length }
