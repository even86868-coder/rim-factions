# Lobby test: A hosts a quick-test game, B joins from the main menu (no quick test) so the lobby can pull it in.
# A hosts from ~/rimworld-test-C, B joins over loopback TCP from ~/rimworld-test-D. Set each profile's
# Config/ModsConfig.xml to the mods under test first; the launcher adds the Rim Factions stack.
$g = "D:\SteamLibrary\steamapps\common\RimWorld"
$launcher = "$env:LOCALAPPDATA\RimFactions\Launcher\RimFactionsLauncher.exe"
$testDir = "$env:LOCALAPPDATA\RimFactions\test"
New-Item -ItemType Directory -Force $testDir | Out-Null
Remove-Item "$testDir\A.log", "$testDir\B.log" -ErrorAction SilentlyContinue
$a = "$env:USERPROFILE\rimworld-test-C"
$b = "$env:USERPROFILE\rimworld-test-D"
Start-Process $launcher -ArgumentList "--game", "`"$g`"", "--host", "--savedatafolder", "`"$a`"", "--no-wait", "--extra", "`"-quicktest -rimfactions-test=A -username=PlayerA`""
Start-Sleep 100
Start-Process $launcher -ArgumentList "--game", "`"$g`"", "--join", "tcp:127.0.0.1:30602", "--savedatafolder", "`"$b`"", "--no-wait", "--extra", "`"-rimfactions-test=B -username=PlayerB`""
