# Builds Rim Factions and assembles build/package (the release layout) and build/RimFactions-<version>.zip.
param([string]$Version = "0.15.3")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$tools = "$env:USERPROFILE\.melty-tools"
$env:DOTNET_ROOT = "$tools\dotnet"; $env:PATH = "$tools\dotnet;$env:PATH"
$dl = "$tools\dl"

& dotnet build "$root\src\RimFactions\RimFactions.csproj" -c Release -nologo -v q
if ($LASTEXITCODE) { throw "mod build failed" }
& dotnet build "$root\src\Launcher\Launcher.csproj" -c Release -nologo -v q
if ($LASTEXITCODE) { throw "launcher build failed" }

$pkg = "$root\build\package"
if (Test-Path $pkg) { Remove-Item -Recurse -Force $pkg }
New-Item -ItemType Directory -Force "$pkg\Mods", "$pkg\Launcher" | Out-Null

# Rim Factions mod folder
Copy-Item -Recurse "$root\mod\RimFactions" "$pkg\Mods\RimFactions"
New-Item -ItemType Directory -Force "$pkg\Mods\RimFactions\1.6\Assemblies" | Out-Null
Copy-Item "$root\build\RimFactions\RimFactions.dll" "$pkg\Mods\RimFactions\1.6\Assemblies\"
Copy-Item "$root\LICENSE" "$pkg\Mods\RimFactions\"

# Bundled MIT dependencies (unchanged releases)
Copy-Item -Recurse "$dl\mpbeta\x\Multiplayer" "$pkg\Mods\Multiplayer"
Copy-Item -Recurse "$dl\mpc\x\Multiplayer-Compatibility" "$pkg\Mods\MultiplayerCompatibility"
$pp = "$dl\pp\x\Prepatcher-master"
New-Item -ItemType Directory -Force "$pkg\Mods\Prepatcher" | Out-Null
foreach ($item in "About", "Assemblies", "1.5", "1.4", "LoadFolders.xml", "LICENSE", "README.md") { Copy-Item -Recurse "$pp\$item" "$pkg\Mods\Prepatcher\" }
Copy-Item "$dl\mp\src\Multiplayer-0.11.5\LICENSE" "$pkg\Mods\Multiplayer\LICENSE"
Invoke-WebRequest https://raw.githubusercontent.com/rwmt/Multiplayer-Compatibility/master/LICENSE -OutFile "$pkg\Mods\MultiplayerCompatibility\LICENSE"
# Workshop ids would make Steam treat our local copies as workshop items; drop them.
Get-ChildItem $pkg -Recurse -Filter PublishedFileId.txt | Remove-Item -Force
Get-ChildItem $pkg -Recurse -Filter *.pdb | Remove-Item -Force

Copy-Item "$root\build\Launcher\RimFactionsLauncher.exe" "$pkg\Launcher\"
Copy-Item "$root\README.md", "$root\CREDITS.md" "$pkg\"

# Repository leftovers that aren't part of the mods
Get-ChildItem $pkg -Recurse -Force | Where-Object { $_.Name -in ".git", "update_l10n_tags.py" } | Remove-Item -Recurse -Force
foreach ($old in "1.4", "1.5") { Remove-Item -Recurse -Force "$pkg\Mods\Prepatcher\$old" }

# Steam Workshop copy: just the Rim Factions mod folder (its About.xml points Steam at the dependencies).
$ws = "$root\build\workshop\RimFactions"
if (Test-Path "$root\build\workshop") { Remove-Item -Recurse -Force "$root\build\workshop" }
Copy-Item -Recurse "$pkg\Mods\RimFactions" $ws
Copy-Item "$root\README.md", "$root\CREDITS.md" $ws

$zip = "$root\build\RimFactions-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, "Create")
foreach ($f in Get-ChildItem $pkg -Recurse -File) {
  $name = $f.FullName.Substring($pkg.Length + 1).Replace("\", "/")
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name) | Out-Null
}
$archive.Dispose()
Write-Output "built $zip"
