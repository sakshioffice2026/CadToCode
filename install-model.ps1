$ErrorActionPreference = "Stop"

$fileName = "Phi-3-mini-4k-instruct-q4.gguf"
$url = "https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-gguf/resolve/main/$fileName"

$webProject = Join-Path $PSScriptRoot "Web"
$sourceDir = Join-Path $webProject "Models"
$sourceFile = Join-Path $sourceDir $fileName

New-Item -ItemType Directory -Force -Path $sourceDir | Out-Null

if (-not (Test-Path $sourceFile)) {
    Write-Host "Downloading $fileName (about 2.3 GB)..."
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri $url -OutFile $sourceFile
}

$outputs = Get-ChildItem -Path (Join-Path $webProject "bin") -Recurse -Filter "Web.dll" -ErrorAction SilentlyContinue |
    ForEach-Object { $_.Directory.FullName } |
    Sort-Object -Unique

if (-not $outputs) {
    $outputs = @((Join-Path $webProject "bin\Debug\net8.0"))
}

foreach ($dir in $outputs) {
    $target = Join-Path $dir "Models"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Path $sourceFile -Destination (Join-Path $target $fileName) -Force
    Write-Host "Model placed in $target"
}

Write-Host "Done."
