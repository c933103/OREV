param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish App/App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false -p:DebugType=None -o "out/$Runtime" -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Get-ChildItem "out/$Runtime" -Filter '*.pdb' | Remove-Item
    Copy-Item LICENSE,NOTICE,LICENSING.md "out/$Runtime"
    Copy-Item notices "out/$Runtime" -Recurse -Force
    Write-Host "Portable application: $PSScriptRoot/out/$Runtime/OpenRouter Export Viewer.exe"
} finally { Pop-Location }
