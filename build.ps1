param(
    [string]$Runtime = "win-x64",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$csproj = "src\AgentIsland\AgentIsland.csproj"
if (-not $Version) {
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version
}
if (-not $Version) { $Version = "0.0.0" }
$version = "$Version".Trim()

$publishDir = "dist\publish"
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

& dotnet publish $csproj `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$zip = "dist\AgentIsland-$version-$Runtime.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $publishDir "AgentIsland.exe") -DestinationPath $zip

Write-Output "built  $(Join-Path $publishDir 'AgentIsland.exe')"
Write-Output "packed $zip"
