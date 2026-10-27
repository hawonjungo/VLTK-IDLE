# Đóng gói VLMulti thành 1 file chạy độc lập: dist\VLMulti\VLMulti.exe
# Cách chạy: powershell -ExecutionPolicy Bypass -File publish.ps1
# Dữ liệu (profile game, mật khẩu đã lưu, cài đặt) nằm ở dist\VLMulti\data và được giữ nguyên khi đóng gói lại.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'dist\VLMulti'

if (Get-Process VLMulti -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" }) {
    throw 'VLMulti trong dist đang chạy – hãy tắt app trước khi đóng gói lại.'
}

dotnet test tests\VLMulti.Tests -nologo
if ($LASTEXITCODE -ne 0) { throw 'Test thất bại, dừng đóng gói.' }

# Xoá bản đóng gói cũ nhưng GIỮ thư mục data (phiên đăng nhập, mật khẩu, cài đặt)
if (Test-Path $out) {
    Get-ChildItem $out | Where-Object { $_.Name -ne 'data' } | Remove-Item -Recurse -Force
}

dotnet publish src\VLMulti.Desktop -c Release -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw 'Đóng gói thất bại.' }

$exe = Get-Item (Join-Path $out 'VLMulti.exe')
Write-Host ("Xong: {0} ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB))
