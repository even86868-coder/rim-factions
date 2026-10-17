# Local two-player test: instance A hosts the planet (normal save folder), instance B joins over loopback TCP
# with its own save folder and Multiplayer username. Both start on a quick-test map.
param([switch]$OnlyB)
$g = "D:\SteamLibrary\steamapps\common\RimWorld"
$launcher = "$env:LOCALAPPDATA\RimFactions\Launcher\RimFactionsLauncher.exe"
$testDir = "$env:LOCALAPPDATA\RimFactions\test"
New-Item -ItemType Directory -Force $testDir | Out-Null
if (-not $OnlyB) {
  Remove-Item "$testDir\A.log" -ErrorAction SilentlyContinue
  Start-Process $launcher -ArgumentList "--game", "`"$g`"", "--host", "--no-wait", "--extra", "`"-quicktest -rimfactions-test=A -username=PlayerA`""
  Start-Sleep 75
}
$bData = "$env:USERPROFILE\rimworld-test-B"
New-Item -ItemType Directory -Force $bData | Out-Null
Remove-Item "$testDir\B.log" -ErrorAction SilentlyContinue
Start-Process $launcher -ArgumentList "--game", "`"$g`"", "--join", "tcp:127.0.0.1:30602", "--savedatafolder", "`"$bData`"", "--no-wait", "--extra", "`"-quicktest -rimfactions-test=B -username=PlayerB`""
