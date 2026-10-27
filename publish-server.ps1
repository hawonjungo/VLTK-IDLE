# Đóng gói VLMulti.Server (chạy được trên Linux và Windows) vào dist\:
#   VLMulti.Server-linux-x64.tar.gz    VPS Linux thường (chip Intel/AMD)
#   VLMulti.Server-linux-arm64.tar.gz  VPS ARM, ví dụ Oracle Cloud miễn phí (Ampere)
#   VLMulti.Server-win-x64\            Windows
# Cách chạy: powershell -ExecutionPolicy Bypass -File publish-server.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test tests\VLMulti.Tests -nologo
if ($LASTEXITCODE -ne 0) { throw 'Test thất bại, dừng đóng gói.' }

foreach ($rid in 'linux-x64', 'linux-arm64', 'win-x64') {
    $out = Join-Path $PSScriptRoot "dist\VLMulti.Server-$rid"
    # Xoá bản đóng gói cũ nhưng GIỮ thư mục data (mật khẩu web, mật khẩu game đã mã hoá, dữ liệu trình duyệt của acc)
    if (Test-Path $out) { Get-ChildItem $out | Where-Object { $_.Name -ne 'data' } | Remove-Item -Recurse -Force }
    dotnet publish src\VLMulti.Server -c Release -r $rid --self-contained true -p:DebugType=none -o $out -nologo
    if ($LASTEXITCODE -ne 0) { throw "Đóng gói $rid thất bại." }

    if ($rid -like 'linux-*') {
        # Kèm bộ cài cho VPS Linux, đổi xuống dòng kiểu Linux để chạy được bằng bash
        New-Item -ItemType Directory -Force "$out\deploy" | Out-Null
        foreach ($f in 'install.sh', 'vlmulti.service') {
            $text = [IO.File]::ReadAllText("$PSScriptRoot\deploy\linux\$f") -replace "`r`n", "`n"
            [IO.File]::WriteAllText("$out\deploy\$f", $text, (New-Object Text.UTF8Encoding $false))
        }
        Copy-Item "$out\deploy\install.sh" "$out\install.sh"
        $tar = Join-Path $PSScriptRoot "dist\VLMulti.Server-$rid.tar.gz"
        if (Test-Path $tar) { Remove-Item $tar }
        # Không đóng gói dữ liệu riêng (nếu từng chạy thử trong thư mục này)
        tar -czf $tar --exclude "VLMulti.Server-$rid/data" -C (Join-Path $PSScriptRoot 'dist') "VLMulti.Server-$rid"
        Write-Host ("Xong: {0} ({1:N0} MB)" -f $tar, ((Get-Item $tar).Length / 1MB))
    }
    else {
        Write-Host "Xong: $out"
    }
}
