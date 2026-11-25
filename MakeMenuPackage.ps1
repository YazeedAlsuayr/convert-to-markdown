# Builds the small signed package that puts "Convert to Markdown" at the top of the Windows 11 right-click menu.
# Uses makeappx.exe and signtool.exe from the Windows SDK (installed with Visual Studio).
# The signing certificate is made once and kept in %LOCALAPPDATA%\ConvertToMarkdown\signing.
$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$out    = Join-Path $here 'menu-package'
$stage  = Join-Path $out 'stage'
$msix   = Join-Path $out 'ConvertToMarkdown.Menu.msix'
$cerOut = Join-Path $out 'ConvertToMarkdown.cer'
$sign   = Join-Path $env:LOCALAPPDATA 'ConvertToMarkdown\signing'
$pfx    = Join-Path $sign 'ConvertToMarkdown.pfx'
$cer    = Join-Path $sign 'ConvertToMarkdown.cer'
$pass   = 'convert-to-markdown-local'

# makeappx.exe and signtool.exe: from the downloaded Microsoft.Windows.SDK.BuildTools package, or an installed Windows SDK
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$places = @((Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools'), (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin')) | Where-Object { Test-Path $_ }
function Find-Tool($name) {
    foreach ($p in $places) {
        $t = Get-ChildItem -Path $p -Recurse -Filter $name -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($t) { return $t }
    }
    return $null
}
$makeappx = Find-Tool 'makeappx.exe'
$signtool = Find-Tool 'signtool.exe'
if (-not $makeappx -or -not $signtool) {
    Write-Host 'makeappx.exe / signtool.exe not found: Setup will not include the top-of-menu entry (Open with still works).'
    if (Test-Path $msix) { Remove-Item $msix -Force }
    exit 0
}
Write-Host ('Using ' + $makeappx.FullName)

# 1. the signing certificate (made once)
New-Item -ItemType Directory -Force -Path $sign | Out-Null
if (-not (Test-Path $pfx)) {
    Write-Host 'Making the signing certificate (once)...'
    $c = New-SelfSignedCertificate -Type Custom -Subject 'CN=ConvertToMarkdown' -KeyUsage DigitalSignature `
         -FriendlyName 'Convert to Markdown package' -CertStoreLocation 'Cert:\CurrentUser\My' `
         -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(10)
    $secure = ConvertTo-SecureString -String $pass -Force -AsPlainText
    Export-PfxCertificate -Cert $c -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $c -FilePath $cer | Out-Null
}

# 2. pack and sign
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $here 'Package\*') $stage -Recurse
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx.FullName pack /d $stage /p $msix /nv /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
& $signtool.FullName sign /fd SHA256 /f $pfx /p $pass $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
Copy-Item $cer $cerOut -Force
Write-Host 'Main right-click menu package ready.'
