# Runs the unit tests.
#
# Why the staging copy: .NET Framework resolves a test assembly with
# Assembly.LoadFrom, and on some machines/volumes it treats an assembly under the repo
# as "loaded from a network location" and refuses it with HRESULT 0x80131515
# ("loadFromRemoteSources"). VSTest then reports that no tests were found, because the
# xunit adapter itself could not be loaded. Copying the build output to a local temp
# directory sidesteps the check without changing what is tested. `dotnet test` works
# directly on machines that do not hit this.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests\QuickLook.Plugin.AudioViewer.Tests\QuickLook.Plugin.AudioViewer.Tests.csproj'
$output = Join-Path $root 'tests\QuickLook.Plugin.AudioViewer.Tests\bin\Release\net48'
$stage = Join-Path $env:TEMP 'ql-audioviewer-tests'

dotnet build $project -c Release -v:m --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item (Join-Path $output '*') $stage -Recurse -Force

dotnet vstest (Join-Path $stage 'QuickLook.Plugin.AudioViewer.Tests.dll')
exit $LASTEXITCODE
