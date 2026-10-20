# Captures one RimWorld window (by process id) to a PNG. Only that window's rectangle is captured.
param([int]$ProcessId, [string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RFWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@
$p = Get-Process -Id $ProcessId
$h = $p.MainWindowHandle
$r = New-Object RFWin+RECT
for ($i = 0; $i -lt 10; $i++) {
  [RFWin]::ShowWindow($h, 9) | Out-Null
  [RFWin]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 1500
  [RFWin]::GetWindowRect($h, [ref]$r) | Out-Null
  if (($r.R - $r.L) -gt 800) { break }
}
# A capture taken before the game redraws comes out blank: check a few pixels and try again (up to 5 times).
for ($try = 0; $try -lt 5; $try++) {
  Start-Sleep -Milliseconds 800
  $w = $r.R - $r.L; $hgt = $r.B - $r.T
  $bmp = New-Object Drawing.Bitmap $w, $hgt
  $g = [Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  $ok = [RFWin]::PrintWindow($h, $hdc, 2)
  $g.ReleaseHdc($hdc)
  if (-not $ok) { $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size) }
  $g.Dispose()
  $colors = @{}
  foreach ($fx in 0.2, 0.4, 0.5, 0.6, 0.8) { foreach ($fy in 0.2, 0.5, 0.8) { $colors[$bmp.GetPixel([int]($w * $fx), [int]($hgt * $fy)).ToArgb()] = 1 } }
  if ($w -gt 800 -and $colors.Count -gt 2) { break }
  $bmp.Dispose(); $bmp = $null
  [RFWin]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 1500
  [RFWin]::GetWindowRect($h, [ref]$r) | Out-Null
}
if ($bmp -eq $null) { "capture stayed blank"; exit 1 }
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved $Out ($w x $hgt)"
