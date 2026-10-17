# Puts the current build into release/ for the Rim Factions Updater (it reads release/version.txt and
# release/RimFactions.zip from the main branch on GitHub). Run build.ps1 first, then commit and push.
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\mod\RimFactions\About\About.xml")).ModMetaData.modVersion
New-Item -ItemType Directory -Force "$root\release" | Out-Null
Copy-Item -Force "$root\build\RimFactions-$version.zip" "$root\release\RimFactions.zip"
Set-Content "$root\release\version.txt" $version -Encoding ASCII -NoNewline
if (Test-Path "$root\build\Updater\RimFactionsUpdater.exe") { Copy-Item -Force "$root\build\Updater\RimFactionsUpdater.exe" "$root\release\" }
"release/ now holds $version"
