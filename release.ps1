# Phát hành một phiên bản: biên dịch, đóng gói .zip và đăng lên GitHub Releases.
# Ứng dụng tự cập nhật bằng cách tải file .zip của bản "Latest".
# Cách dùng:  .\release.ps1 -Version 2.10.0 -Title "Cài đặt người dùng" -Notes "Có gì mới..."
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Title = "",
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$repo = "bentarofficial/bentar-second-brain"
$name = "Bentar Second Brain"

# Số phiên bản trong mã phải khớp tag, nếu không app sẽ không nhận ra bản mới
$ver = Select-String -Path src\*.cs -Pattern 'AssemblyVersion\("([0-9.]+)"\)' | Select-Object -First 1
if (-not $ver -or -not $ver.Matches[0].Groups[1].Value.StartsWith($Version)) {
    throw "AssemblyVersion trong mã ($($ver.Matches[0].Groups[1].Value)) chưa khớp $Version. Sửa rồi chạy lại."
}

$out = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out "$name.exe"
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize /win32icon:src\app.ico "/out:$exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll `
    /r:System.IO.Compression.dll /r:System.Security.dll src\*.cs
if ($LASTEXITCODE -ne 0) { throw "Biên dịch lỗi." }

$zip = Join-Path $out ("Bentar-Second-Brain-v$Version.zip")
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $exe, README.md -DestinationPath $zip

$tag = "v$Version"
if (-not $Title) { $Title = $tag } else { $Title = "$tag – $Title" }
gh release create $tag $zip -R $repo --title $Title --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "Tạo release lỗi." }
Write-Host "Đã phát hành $tag: $zip"
