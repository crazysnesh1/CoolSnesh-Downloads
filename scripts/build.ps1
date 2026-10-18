$ErrorActionPreference = 'Stop'

# Resolve repository root from this script's location so it works when invoked
# from the repository root, VS Code terminal, or another working directory.
$ProjectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $ProjectRoot
try {
    Write-Host 'Restoring NuGet packages...'
    dotnet restore .\CoolSnesh.Avalonia.csproj

    Write-Host 'Building CoolSnesh Downloads...'
    dotnet build .\CoolSnesh.Avalonia.csproj --configuration Release

    Write-Host 'Build finished. Output is under .\bin\Release\net10.0-windows\.'
}
finally {
    Pop-Location
}
