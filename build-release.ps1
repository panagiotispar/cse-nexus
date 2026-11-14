# Φτιάχνει το πακέτο release τοπικά, με την ίδια διαδικασία που κάνει και το GitHub Actions.
#
# Χρήση (σε PowerShell, μέσα στον φάκελο της λύσης):
#   powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -Version 0.1.0
#
# Αποτέλεσμα: ο φάκελος "release" με το zip και το SHA256SUMS.txt, έτοιμα για ανέβασμα στα GitHub Releases.

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# 1) Βρες το έργο
$proj = Get-ChildItem -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|publish|release)[\\/]' } |
    Select-Object -First 1
if (-not $proj) { throw 'Δεν βρέθηκε αρχείο .csproj' }
Write-Host "Έργο: $($proj.FullName)  |  Έκδοση: $Version"

$publish = Join-Path $root 'publish'
$release = Join-Path $root 'release'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $release -Force | Out-Null

# 2) Build (self-contained, ένα εκτελέσιμο)
dotnet publish $proj.FullName -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Το build απέτυχε. Δες τα μηνύματα παραπάνω.' }

# 3) Έλεγχος ότι το πακέτο είναι πλήρες
$exe = Get-ChildItem $publish -Filter *.exe
if (-not $exe) { throw 'Δεν δημιουργήθηκε εκτελέσιμο (.exe) στον φάκελο publish' }
foreach ($f in 'schedule.html', 'focus.html', 'notes.html') {
    if (-not (Test-Path (Join-Path $publish $f))) {
        throw "Λείπει το $f από το πακέτο. Έλεγξε ότι έχει CopyToOutputDirectory στο .csproj"
    }
}
$sizeMb = [math]::Round($exe.Length / 1MB, 1)
Write-Host "Εκτελέσιμο: $($exe.Name) ($sizeMb MB). Όλα τα αρχεία υπάρχουν."

# 4) Πακετάρισμα + άθροισμα ελέγχου
$zipName = "UOI-Study-Hub-v$Version-win-x64.zip"
$zipPath = Join-Path $release $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zipPath

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
"$hash  $zipName" | Out-File -FilePath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii

Write-Host ''
Write-Host 'Έτοιμο! Αρχεία για ανέβασμα στο GitHub Release:' -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  $(Join-Path $release 'SHA256SUMS.txt')"
