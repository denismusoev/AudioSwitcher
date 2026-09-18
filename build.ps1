param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot/tests/AudioSwitcher.Checks" --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
dotnet publish "$PSScriptRoot/src/AudioSwitcher/AudioSwitcher.csproj" --configuration Release --runtime $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false --output "$PSScriptRoot/artifacts/portable/$Runtime"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath "$PSScriptRoot/README.md" -Destination "$PSScriptRoot/artifacts/portable/$Runtime/README.md"
Compress-Archive -Path "$PSScriptRoot/artifacts/portable/$Runtime/*" -DestinationPath "$PSScriptRoot/artifacts/AudioSwitcher-$Runtime.zip" -Force
