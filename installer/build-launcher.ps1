param([string]$OutputName = "JAGO-Launcher.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$project = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $project 'dist'
$payload = Join-Path $PSScriptRoot 'payload'
$output = Join-Path $dist $OutputName
$manifestPath = Join-Path $dist 'manifest.json'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$sourcePackages = @('JAGO.zip', 'Gears.zip', 'Quartz.zip', 'Russian.zip', 'ZZTong.zip', 'GardenDecor.zip', 'GardenDecorSeatLook.zip')
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$stage = New-Item -ItemType Directory -Path (Join-Path $env:TEMP ('JAGO-build-' + [Guid]::NewGuid().ToString('N')))
try {
    $packages = @(
        foreach ($sourceName in $sourcePackages) {
            $source = [IO.Compression.ZipFile]::OpenRead((Join-Path $payload $sourceName))
            try {
                $groups = $source.Entries | Where-Object { $_.Name -and $_.FullName.Contains('/') } |
                    Group-Object { $_.FullName.Split('/')[0] }
                foreach ($group in $groups) {
                    $folder = $group.Name
                    if ($folder -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$' -or
                        -not ($group.Group.FullName -contains "$folder/ModInfo.xml")) {
                        throw "Invalid mod folder $folder in $sourceName"
                    }
                    $name = "$folder.zip"
                    $path = Join-Path $stage.FullName $name
                    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
                    try {
                        foreach ($entry in ($group.Group | Sort-Object FullName)) {
                            $copy = $archive.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                            $copy.LastWriteTime = $entry.LastWriteTime
                            $inputStream = $entry.Open()
                            try {
                                $outputStream = $copy.Open()
                                try { $inputStream.CopyTo($outputStream) }
                                finally { $outputStream.Dispose() }
                            }
                            finally { $inputStream.Dispose() }
                        }
                    }
                    finally { $archive.Dispose() }
                    [ordered]@{
                        Name = $name
                        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                        Bytes = (Get-Item -LiteralPath $path).Length
                        Folders = @($folder)
                        Url = "https://github.com/gyglia/jago-releases/releases/download/sever2026.1/$name"
                    }
                }
            }
            finally { $source.Dispose() }
        }
    )
    if ($packages.Count -ne 34) { throw 'Expected 34 baseline mod packages' }
    $manifest = [ordered]@{
        Schema = 1
        Version = 'sever2026.1'
        GameVersion = '3.4'
        Notes = 'Базовая клиентская сборка Sever2026.'
        Packages = $packages
    }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

    $arguments = @('/nologo', '/target:winexe', '/define:LAUNCHER', '/platform:anycpu', '/optimize+', '/utf8output',
        '/codepage:65001', "/out:$output", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
        '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
        '/reference:System.Web.Extensions.dll', '/reference:Microsoft.CSharp.dll',
        "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\WindowsBase.dll", '/reference:System.Xaml.dll',
        "/resource:$manifestPath,manifest.json", "/resource:$PSScriptRoot\MoonOutpost.png,MoonOutpost.png",
        "/resource:$PSScriptRoot\MoonOutpost-White.png,MoonOutpost-White.png",
        "/resource:$PSScriptRoot\assets\zombie-gaunt-idle\zombie-idle-sheet.png,ZombieIdle.png",
        "/resource:$PSScriptRoot\assets\SevenDaysButton.png,SevenDaysButton.png",
        "/resource:$PSScriptRoot\assets\RustyGear-Light.png,RustyGear.png",
        "/resource:$PSScriptRoot\assets\BloodProgress.png,BloodProgress.png",
        "/resource:$PSScriptRoot\assets\PorchLamp-Off.png,PorchLamp-Off.png",
        "/resource:$PSScriptRoot\assets\SevenDaysButton-Idle.png,SevenDaysButton-Idle.png",
        "/win32icon:$PSScriptRoot\JAGO.ico", "/resource:$PSScriptRoot\JAGO.ico,JAGO.ico")
    $packageOutput = Join-Path $dist 'packages'
    New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
    foreach ($package in $packages) { Copy-Item -LiteralPath (Join-Path $stage.FullName $package.Name) -Destination (Join-Path $packageOutput $package.Name) -Force }
    foreach ($sourceFile in @('AssemblyInfo.cs', 'Installer.cs', 'LauncherRelease.cs', 'LauncherSchedule.cs', 'LauncherSelfUpdate.cs', 'LauncherManualUpdate.cs', 'LauncherWindow.cs', 'LauncherDesign.cs', 'LauncherPreview.cs', 'LauncherTest.cs')) {
        $arguments += Join-Path $PSScriptRoot $sourceFile
    }
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed' }
    $offlinePath = Join-Path $dist 'Sever2026-offline.zip'
    if (Test-Path -LiteralPath $offlinePath) { Remove-Item -LiteralPath $offlinePath }
    $offlineZip = [IO.Compression.ZipFile]::Open($offlinePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($offlineZip, $manifestPath, 'manifest.json') | Out-Null
        foreach ($package in $packages) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($offlineZip, (Join-Path $stage.FullName $package.Name), $package.Name, [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
        }
    } finally { $offlineZip.Dispose() }
    Get-FileHash -LiteralPath $output -Algorithm SHA256
}
finally {
    Get-ChildItem -LiteralPath $stage.FullName -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    [IO.Directory]::Delete($stage.FullName, $false)
}












