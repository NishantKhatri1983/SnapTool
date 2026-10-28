# Builds the release files: bin\publish\SnapTool.exe (portable, needs .NET 8 Desktop Runtime or newer)
# and bin\installer\SnapTool-Setup-<version>.exe. Needs the .NET 8 SDK and Inno Setup 6 (ISCC.exe).
param([string]$Iscc = "")

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\SnapTool.csproj")).Project.PropertyGroup.Version
$publish = "$root\bin\publish"

dotnet publish "$root\SnapTool.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=none -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno\ISCC.exe",
              "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw "Inno Setup 6 not found; install it from https://jrsoftware.org/isdl.php or pass -Iscc <path>" }

& $Iscc "/DAppVersion=$version" "/DPublishDir=$publish" "$PSScriptRoot\SnapTool.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-ChildItem "$publish\SnapTool.exe", "$root\bin\installer\SnapTool-Setup-$version.exe" |
    ForEach-Object { "{0}  {1:N0} KB  sha256 {2}" -f $_.Name, ($_.Length / 1KB), (Get-FileHash $_ -Algorithm SHA256).Hash.ToLower() }
