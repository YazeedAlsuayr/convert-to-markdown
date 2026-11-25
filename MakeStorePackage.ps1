# Builds the Microsoft Store package of Convert to Markdown.
#   -Test : makes a signed test package you can install on this PC before the Store has the app
#           (uses a test identity and the signing certificate Build.cmd already made and trusted).
#   (no -Test) : makes the package to upload to Partner Center, with the identity from StoreIdentity.txt.
#                It is not signed: Microsoft signs it when the Store accepts it.
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$work  = Join-Path $here 'StoreBuild'
$stage = Join-Path $work 'stage'
$out   = Join-Path $here 'StorePackage'

# ---- 1. identity ----
$id = @{}
Get-Content (Join-Path $here 'StoreIdentity.txt') | Where-Object { $_ -match '^\s*([A-Za-z]+)\s*=\s*(.+?)\s*$' } |
    ForEach-Object { if ($_ -match '^\s*([A-Za-z]+)\s*=\s*(.+?)\s*$') { $id[$Matches[1]] = $Matches[2] } }
if ($Test) {
    $id.Name = 'ConvertToMarkdown.Test'; $id.Publisher = 'CN=ConvertToMarkdown'; $id.PublisherName = 'Convert to Markdown (test)'
}
foreach ($k in 'Name', 'Publisher', 'PublisherName', 'Version') {
    if (-not $id[$k] -or $id[$k] -like 'PASTE*') {
        Write-Host "StoreIdentity.txt: '$k' is not filled in yet. Copy it from Partner Center > Product identity." -ForegroundColor Red
        exit 1
    }
}
if ($id.Version -notmatch '^\d+\.\d+\.\d+\.0$') { Write-Host 'Version must look like 2.0.0.0 (last number 0).' -ForegroundColor Red; exit 1 }
Write-Host ("Package: {0}  {1}  version {2}" -f $id.Name, $id.Publisher, $id.Version)

# ---- 2. tools (makeappx / signtool from the NuGet package Build.cmd downloads, or the Windows SDK) ----
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$places = @((Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools'), (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin')) | Where-Object { Test-Path $_ }
function Find-Tool($name) {
    foreach ($p in $places) {
        $t = Get-ChildItem -Path $p -Recurse -Filter $name -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($t) { return $t.FullName }
    }
    return $null
}
$makeappx = Find-Tool 'makeappx.exe'
$signtool = Find-Tool 'signtool.exe'
if (-not $makeappx) { Write-Host 'makeappx.exe not found. Run Build.cmd once first (it downloads the packaging tools).' -ForegroundColor Red; exit 1 }

# ---- 3. the app (one self-contained file, without the small menu package) ----
Write-Host 'Building the app...'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$asmVersion = $id.Version -replace '\.0$', ''
& dotnet publish (Join-Path $here 'ConvertToMarkdown.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:StoreBuild=true "-p:Version=$asmVersion" -o (Join-Path $work 'app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item (Join-Path $work 'app\ConvertToMarkdown.exe') $stage
Copy-Item (Join-Path $here 'THIRD-PARTY NOTICES.txt') $stage

# ---- 4. the AI engine (llama.cpp, Vulkan build), inside the package; downloaded once and kept in engine-cache ----
$cache = Join-Path $here 'engine-cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$zip = Get-ChildItem $cache -Filter '*.zip' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) {
    Write-Host 'Downloading the AI engine (once)...'
    $rels = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15' -Headers @{ 'User-Agent' = 'ConvertToMarkdown-build' }
    $asset = $null
    foreach ($r in $rels) {
        if ($r.draft) { continue }
        $asset = $r.assets | Where-Object { $_.name -match 'win-vulkan-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
        if ($asset) { break }
    }
    if (-not $asset) { throw 'The AI engine download was not found. Try again later.' }
    $zipPath = Join-Path $cache $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath
    $zip = Get-Item $zipPath
}
Write-Host ('AI engine: ' + $zip.Name)
$engTmp = Join-Path $work 'engine'
Expand-Archive -Path $zip.FullName -DestinationPath $engTmp -Force
$server = Get-ChildItem $engTmp -Recurse -Filter 'llama-server.exe' | Select-Object -First 1
if (-not $server) { throw 'The AI engine download did not contain llama-server.exe' }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'engine') | Out-Null
Copy-Item (Join-Path $server.DirectoryName '*') (Join-Path $stage 'engine') -Recurse

# ---- 5. manifest and pictures ----
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Assets') | Out-Null
Copy-Item (Join-Path $here 'Package\Assets\*') (Join-Path $stage 'Assets')
$m = Get-Content (Join-Path $here 'Store\AppxManifest.xml') -Raw
$esc = { param($v) [System.Security.SecurityElement]::Escape($v) }
$m = $m.Replace('$NAME$', (& $esc $id.Name)).Replace('$PUBLISHER$', (& $esc $id.Publisher)).Replace('$PUBLISHERNAME$', (& $esc $id.PublisherName)).Replace('$VERSION$', $id.Version)
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $m, (New-Object System.Text.UTF8Encoding($false)))

# ---- 5b. picture index (resources.pri) ----
# Without it Windows cannot pick the right picture size, and draws the small logo on a coloured square
# (your accent colour) in the taskbar and Start menu.
$makepri = Find-Tool 'makepri.exe'
if (-not $makepri) { throw 'makepri.exe not found. Run Build.cmd once first (it downloads the packaging tools).' }
foreach ($base in 'Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png') {
    $f = Join-Path $stage "Assets\$base"
    if (Test-Path $f) { Remove-Item $f -Force }      # the .scale-NNN versions replace it
}
$priConfig = Join-Path $work 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed' }
& $makepri new /pr $stage /cf $priConfig /mn (Join-Path $stage 'AppxManifest.xml') /of (Join-Path $stage 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri failed' }

# ---- 6. pack ----
New-Item -ItemType Directory -Force -Path $out | Out-Null
$suffix = if ($Test) { '_test' } else { '' }
$msix = Join-Path $out ("ConvertToMarkdown_{0}_x64{1}.msix" -f $id.Version, $suffix)
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }

if ($Test) {
    $pfx = Join-Path $env:LOCALAPPDATA 'ConvertToMarkdown\signing\ConvertToMarkdown.pfx'
    if (-not (Test-Path $pfx) -or -not $signtool) { throw 'The test certificate was not found. Run Build.cmd and install with its Setup once first.' }
    & $signtool sign /fd SHA256 /f $pfx /p 'convert-to-markdown-local' $msix | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
}
Write-Host ''
Write-Host ('Done: ' + $msix) -ForegroundColor Green
