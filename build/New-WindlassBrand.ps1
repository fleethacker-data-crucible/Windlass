#requires -Version 5.1
<#
.SYNOPSIS
  Renders the brand assets made from source: the mark as PNG, favicon.ico, and the placeholder emblems.
.DESCRIPTION
  Build-machine only. Uses headless Edge (or Chrome) to render assets\brand\mark.svg to PNG with a transparent
  background, then packs PNG frames into favicon.ico. The emblem step renders assets\brand\emblem-source.html (the
  placeholder) to emblem-light.png and emblem-dark.png; delete that source once the detailed original emblem is in,
  and the step is skipped. -MarkOnly skips it in any case. Windlass has no tray icon, so there is no tray.ico.
#>
[CmdletBinding()]
param([switch]$MarkOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$brand = Join-Path $root 'assets\brand'
$browser = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Edge or Chrome is needed on the build machine to render the brand assets.' }
Add-Type -AssemblyName System.Drawing

function Render([string]$url, [string]$out, [int]$w, [int]$h, [int]$scale = 1) {
    $profile = Join-Path ([IO.Path]::GetTempPath()) ('windlass-brand-' + [guid]::NewGuid().ToString('N'))
    $args = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--default-background-color=00000000', "--user-data-dir=$profile", "--window-size=$w,$h", "--force-device-scale-factor=$scale", "--screenshot=$out", $url)
    Start-Process -FilePath $browser -ArgumentList $args -WindowStyle Hidden -Wait | Out-Null
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $out)) { throw ('Rendering failed: ' + $url) }
}

function Resize([string]$src, [string]$out, [int]$size) {
    $img = [System.Drawing.Image]::FromFile($src)
    try {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($img, 0, 0, $size, $size)
        $g.Dispose()
        $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    } finally { $img.Dispose() }
}

function Write-Ico([string]$out, [string[]]$pngs) {
    # ICO with PNG frames (Vista+): header, one directory entry per frame, then the PNG bytes.
    $frames = @($pngs | ForEach-Object { , [IO.File]::ReadAllBytes($_) })
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $img = [System.Drawing.Image]::FromFile($pngs[$i]); $size = $img.Width; $img.Dispose()
        $b = if ($size -ge 256) { 0 } else { $size }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $bw.Write($f) }
    $bw.Flush()
    [IO.File]::WriteAllBytes($out, $ms.ToArray())
    $bw.Dispose()
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('windlass-brand-out-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    # Render the SVG large by wrapping it, so small sizes are downscaled from a sharp source.
    $markUrl = ([Uri](Join-Path $brand 'mark.svg')).AbsoluteUri
    $markBig = Join-Path $tmp 'mark-512.png'
    $wrap = Join-Path $tmp 'mark.html'
    Set-Content -Path $wrap -Encoding UTF8 -Value ('<!doctype html><html><body style="margin:0;background:transparent"><img src="' + $markUrl + '" width="512" height="512" style="display:block"></body></html>')
    Render ([Uri]$wrap).AbsoluteUri $markBig 512 512
    foreach ($s in @(16, 32, 48, 256)) { Resize $markBig (Join-Path $tmp "mark-$s.png") $s }
    Copy-Item (Join-Path $tmp 'mark-256.png') (Join-Path $brand 'mark-256.png') -Force
    Write-Ico (Join-Path $brand 'favicon.ico') @((Join-Path $tmp 'mark-16.png'), (Join-Path $tmp 'mark-32.png'), (Join-Path $tmp 'mark-48.png'), (Join-Path $tmp 'mark-256.png'))
    if (-not $MarkOnly -and (Test-Path (Join-Path $brand 'emblem-source.html'))) {
        foreach ($theme in @('light', 'dark')) {
            $raw = Join-Path $tmp "emblem-$theme-raw.png"
            Render (([Uri](Join-Path $brand 'emblem-source.html')).AbsoluteUri + "?theme=$theme") $raw 512 512 2
            Resize $raw (Join-Path $brand "emblem-$theme.png") 1024
        }
    }
    Write-Host ('Brand assets written to ' + $brand) -ForegroundColor Green
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
