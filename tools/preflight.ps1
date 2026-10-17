# Preflight: every sheet cell filled, every cross-reference resolves, every row implemented, every package present.
$root = Split-Path $PSScriptRoot -Parent
$sheets = Get-Content "$root\design\sheets.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$src = (Get-ChildItem "$root\src" -Recurse -Filter *.cs | Get-Content -Raw) -join "`n"
$keyed = (Get-ChildItem "$root\mod\RimFactions" -Recurse -Filter *.xml | Get-Content -Raw) -join "`n"
$problems = New-Object System.Collections.Generic.List[string]
$kindOf = @{ settings = "setting"; features = "feature"; news = "news"; hooks = "hook"; systems = "system"; packaging = "packaging"; messages = "message"; data = "data" }

$ids = @{}
foreach ($p in $sheets.PSObject.Properties) {
  if ($p.Name.StartsWith("_")) { continue }
  $ids[$kindOf[$p.Name]] = @($p.Value.rows | ForEach-Object { $_.id })
}

foreach ($p in $sheets.PSObject.Properties) {
  if ($p.Name.StartsWith("_")) { continue }
  $sheet = $p.Value
  foreach ($row in $sheet.rows) {
    foreach ($col in $sheet._columns) {
      $v = $row.$col
      if ($null -eq $v -or "$v" -eq "") { $problems.Add("$($p.Name).$($row.id).$($col): empty"); continue }
      foreach ($m in [regex]::Matches("$v", '\b(setting|feature|news|hook|system|message|data):(\w+)')) {
        if ($ids[$m.Groups[1].Value] -notcontains $m.Groups[2].Value) { $problems.Add("$($p.Name).$($row.id).$($col): unresolved $($m.Value)") }
      }
    }
    if ($p.Name -ne "packaging" -and -not $src.Contains($row.id)) { $problems.Add("$($p.Name).$($row.id): not implemented in src/") }
    foreach ($col in "label_key", "text_key") {
      if ($row.PSObject.Properties.Name -contains $col -and -not $keyed.Contains("<$($row.$col)>")) { $problems.Add("$($p.Name).$($row.id).$($col): language key $($row.$col) missing") }
    }
  }
}

foreach ($row in $sheets.packaging.rows) {
  $about = Join-Path "$root\build\package" "$($row.folder)\About\About.xml"
  if (-not (Test-Path $about)) { $problems.Add("packaging.$($row.id): $about missing (run build)") }
  elseif (-not (Get-Content $about -Raw).ToLower().Contains($row.packageId.ToLower())) { $problems.Add("packaging.$($row.id): packageId mismatch") }
}

if ($problems.Count) { "PREFLIGHT PROBLEMS:"; $problems | ForEach-Object { "- $_" }; exit 1 } else { "PREFLIGHT CLEAN" }
