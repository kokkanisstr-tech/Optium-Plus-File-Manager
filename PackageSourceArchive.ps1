$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourceRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$archivePath = Join-Path $sourceRoot 'OptiumPlusFileManager-source.zip'
$tempArchive = Join-Path ([System.IO.Path]::GetTempPath()) ('OptiumPlusFileManager-source-' + [guid]::NewGuid().ToString('N') + '.zip')
$items = [System.Collections.Generic.List[string]]::new()
$topFiles = @(
    '.gitignore', 'app.manifest', 'Directory.Build.props', 'OptiumPlusFileManager.csproj', 'OptiumPlusFileManager.sln',
    'MainForm.cs', 'Program.cs', 'PROJECT_PLAN.md',
    'PackageSourceArchive.ps1', 'README.md'
)
$sourceDirectories = @('Api', 'Application', 'Domain', 'Infrastructure', 'Worker', 'tests', 'demo', 'docs', 'third_party')

foreach ($relativePath in $topFiles) {
    $path = Join-Path $sourceRoot $relativePath
    if (Test-Path -LiteralPath $path -PathType Leaf) { $items.Add($path) }
}

foreach ($relativeDirectory in $sourceDirectories) {
    $directory = Join-Path $sourceRoot $relativeDirectory
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse -Force) {
        $relativePath = $file.FullName.Substring($sourceRoot.Length).TrimStart([char[]]@('\', '/'))
        if ($relativePath -match '(?:^|[\\/])(?:bin|obj|publish|\.vs)(?:[\\/]|$)') { continue }
        $items.Add($file.FullName)
    }
}

try {
    $archive = [System.IO.Compression.ZipFile]::Open($tempArchive, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($filePath in $items) {
            $relativePath = $filePath.Substring($sourceRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $filePath, $relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }

    $check = [System.IO.Compression.ZipFile]::OpenRead($tempArchive)
    try {
        $entries = @($check.Entries | ForEach-Object FullName)
        $required = @('PROJECT_PLAN.md', 'third_party/THIRD_PARTY_NOTICES.md',
            'third_party/tesseract/tessdata/eng.traineddata', 'third_party/tesseract/tessdata/rus.traineddata',
            'third_party/licenses/System.Drawing.Common-LICENSE.txt', 'demo/samples/Скан-конспект.pdf',
            'Directory.Build.props', 'Domain/OptiumPlusFileManager.Domain.csproj',
            'Application/OptiumPlusFileManager.Application.csproj', 'Infrastructure/OptiumPlusFileManager.Infrastructure.csproj',
            'Worker/OptiumPlusFileManager.Worker.csproj', 'Api/OptiumPlusFileManager.Api.csproj',
            'tests/OptiumPlusFileManager.UnitTests/OptiumPlusFileManager.UnitTests.csproj',
            'tests/OptiumPlusFileManager.IntegrationTests/OptiumPlusFileManager.IntegrationTests.csproj')
        foreach ($path in $required) {
            if ($entries -notcontains $path) { throw "Source archive is missing $path" }
        }
        $obsolete = @('AppSettings.cs', 'FileSorterRuntime.cs', 'FileTypeClassifier.cs', 'Infrastructure/FileWatchService.cs')
        foreach ($path in $obsolete) {
            if ($entries -contains $path) { throw "Source archive contains obsolete layout entry $path" }
        }
        if ($entries | Where-Object { $_ -match '(^|/)(bin|obj|publish)/' }) {
            throw 'Source archive unexpectedly contains build or publish output.'
        }
    }
    finally { $check.Dispose() }

    [System.IO.File]::Copy($tempArchive, $archivePath, $true)
    Get-Item -LiteralPath $archivePath | Select-Object FullName, Length, LastWriteTime
    "Included source files: $($items.Count)"
}
finally {
    if (Test-Path -LiteralPath $tempArchive -PathType Leaf) { Remove-Item -LiteralPath $tempArchive -Force }
}
