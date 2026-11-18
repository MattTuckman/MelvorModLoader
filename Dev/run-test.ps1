# Developer tool: builds the test harness, runs the game in the background with MML_TEST=1,
# waits for it to finish and prints the test output. The player's own running game is not touched.
#
#   -Window   render for real (needed for screenshots). The game is started on a separate, hidden
#             Windows desktop, so its window never appears on screen and cannot take keyboard focus.
#             Without it the game runs headless (no window, no rendering).
#   -Only x   passed to the harness as MML_ONLY (e.g. "lang", "shots") to run one group of checks.
#
# The game saves its display settings while it runs, and a headless or off-screen run would leave
# a useless window size behind. So the player's settings files are copied before the run and put
# back afterwards, whatever happens.
param([int]$TimeoutSec = 180, [switch]$Window, [string]$Only = '')

$src  = Split-Path -Parent $PSScriptRoot
$game = Split-Path -Parent $src
$out  = Join-Path $PSScriptRoot 'test-out.txt'
$mod  = Join-Path $game 'mods\_TestHarness'

dotnet build (Join-Path $PSScriptRoot 'TestHarness\TestHarness.csproj') -c Release -o $mod --nologo -v q 2>&1 |
    Select-String -Pattern ' error ' | ForEach-Object { ($_.Line -replace '\[.*$', '' -replace '^.*\\', '').Trim() } | Select-Object -Unique -First 12
if (-not (Test-Path (Join-Path $mod 'TestHarness.dll'))) { 'DERLEME BASARISIZ'; exit 1 }

# --- keep the player's settings safe ---
$userDir   = Join-Path $env:APPDATA 'Games By Malcs\Melvor Idle 2'
$deviceDir = Join-Path $userDir 'device_settings'
$keep      = Join-Path $env:TEMP ('mml-settings-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $keep | Out-Null
$hadDeviceDir = Test-Path $deviceDir
if ($hadDeviceDir) { Copy-Item (Join-Path $deviceDir '*') $keep -Force }
$userSettings = Get-ChildItem (Join-Path $userDir 'steam') -Recurse -Filter 'userSettings.json' -ErrorAction SilentlyContinue
foreach ($f in $userSettings) { Copy-Item $f.FullName (Join-Path $keep ('user-' + $f.Directory.Name + '.json')) -Force }

# --- settings for the test run only: a plain 1600x900 window (on the hidden desktop) ---
if ($Window) {
    New-Item -ItemType Directory -Force -Path $deviceDir | Out-Null
    @'
{
  "Display": {
    "ScreenMode": 1,
    "DisplaySize": { "Width": 1600, "Height": 900 },
    "Screen": -1,
    "WindowPosition": { "X": 100, "Y": 60, "Width": 1600, "Height": 900 },
    "AlwaysOnTop": false, "KeepDeviceScreenOn": false, "VSync": true,
    "ParticleEffects": true, "Animations": true, "UIScale": 1, "MaxFPS": 60
  },
  "Debug": { "LoadLocalDataPackages": false }
}
'@ | Set-Content -Path (Join-Path $deviceDir 'deviceSettings.json') -Encoding ascii
    @'
config_version=5

[display]

window/size/mode=0
window/size/initial_position_type=0
window/size/initial_position=Vector2i(100, 60)
window/size/initial_screen=-1
window/size/borderless=false
window/size/window_width_override=1600
window/size/window_height_override=900
'@ | Set-Content -Path (Join-Path $deviceDir 'override.cfg') -Encoding ascii
}

Set-Content -Path (Join-Path $game 'steam_appid.txt') -Value '3218350' -Encoding ascii
$env:MML_TEST = '1'; $env:MML_TEST_OUT = $out; $env:MML_ONLY = $Only
if (Test-Path $out) { Remove-Item $out -Force }
$exe = Join-Path $game 'Melvor2.exe'
try {
    if ($Window) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class HiddenDesktop {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
  [StructLayout(LayoutKind.Sequential)]
  struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
  [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
  [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static string Run(string exe, string args, string cwd, int timeoutMs) {
    IntPtr desk = CreateDesktopW("MelvorModTest", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
    if (desk == IntPtr.Zero) return "gizli masaustu olusturulamadi: " + Marshal.GetLastWin32Error();
    try {
      STARTUPINFO si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO)); si.lpDesktop = "WinSta0\\MelvorModTest";
      PROCESS_INFORMATION pi;
      StringBuilder cmd = new StringBuilder("\"" + exe + "\" " + args);
      if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, cwd, ref si, out pi)) return "oyun baslatilamadi: " + Marshal.GetLastWin32Error();
      string result;
      if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0) { TerminateProcess(pi.hProcess, 99); result = "ZAMAN ASIMI: test oyunu kapatildi"; }
      else { uint code; GetExitCodeProcess(pi.hProcess, out code); result = "cikis kodu: " + code; }
      CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
      return result;
    } finally { CloseDesktop(desk); }
  }
}
'@
        [HiddenDesktop]::Run($exe, '--audio-driver Dummy', $game, $TimeoutSec * 1000)
    }
    else {
        $p = Start-Process -FilePath $exe -ArgumentList @('--audio-driver', 'Dummy', '--headless') -WorkingDirectory $game -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit($TimeoutSec * 1000)) { Stop-Process -Id $p.Id -Force; 'ZAMAN ASIMI: test oyunu kapatildi' }
        else { "cikis kodu: $($p.ExitCode)" }
    }
}
finally {
    Remove-Item (Join-Path $game 'steam_appid.txt') -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Remove-Item $mod -Recurse -Force -ErrorAction SilentlyContinue
    $env:MML_TEST = $null; $env:MML_TEST_OUT = $null; $env:MML_ONLY = $null

    # --- put the player's settings back exactly as they were ---
    if (Test-Path $deviceDir) { Get-ChildItem $deviceDir -File | Remove-Item -Force -ErrorAction SilentlyContinue }
    if ($hadDeviceDir) {
        Get-ChildItem $keep -File | Where-Object { $_.Name -notlike 'user-*' } | ForEach-Object { Copy-Item $_.FullName (Join-Path $deviceDir $_.Name) -Force }
    }
    foreach ($f in $userSettings) {
        $saved = Join-Path $keep ('user-' + $f.Directory.Name + '.json')
        if (Test-Path $saved) { Copy-Item $saved $f.FullName -Force }
    }
    Remove-Item $keep -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path $out) { Get-Content $out } else { 'test ciktisi yok' }
