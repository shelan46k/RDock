# RDock 單檔發佈（win-x64, self-contained）
# 用法：在專案根目錄執行  .\scripts\publish.ps1

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

$outDir = Join-Path $root "publish\single"
$csproj = Join-Path $root "RDock.csproj"

Write-Host "==> Publishing single-file RDock.exe ..." -ForegroundColor Cyan
dotnet publish $csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $outDir

$exe = Join-Path $outDir "RDock.exe"
if (-not (Test-Path $exe)) {
  throw "Publish failed: $exe not found"
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> OK: $exe ($sizeMb MB)" -ForegroundColor Green
Write-Host "Next: open installer\RDock.iss in Inno Setup and Compile."
