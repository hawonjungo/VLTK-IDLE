#!/usr/bin/env bash
# Cài VLMulti.Server lên VPS Linux (Ubuntu/Debian). Chạy bằng root, từ thư mục đã giải nén:
#   sudo ./install.sh
# Cài lại / cập nhật bản mới: chạy lại lệnh này (dữ liệu trong /var/lib/vlmulti được giữ nguyên).
set -euo pipefail

APP_DIR=/opt/vlmulti
DATA_DIR=/var/lib/vlmulti/data
SRC_DIR="$(cd "$(dirname "$0")" && pwd)"
export PLAYWRIGHT_BROWSERS_PATH="$APP_DIR/ms-playwright"

if [ "$(id -u)" -ne 0 ]; then echo "Hãy chạy bằng root: sudo ./install.sh"; exit 1; fi

echo "== Tạo người dùng hệ thống 'vlmulti' (chạy app không cần quyền root)"
id vlmulti >/dev/null 2>&1 || useradd --system --create-home --home-dir /var/lib/vlmulti --shell /usr/sbin/nologin vlmulti

echo "== Chép app vào $APP_DIR"
systemctl stop vlmulti 2>/dev/null || true
mkdir -p "$APP_DIR" "$DATA_DIR"
if [ "$SRC_DIR" != "$APP_DIR" ]; then
  find "$APP_DIR" -mindepth 1 -maxdepth 1 ! -name ms-playwright -exec rm -rf {} +
  cp -r "$SRC_DIR"/. "$APP_DIR"/
fi
chmod +x "$APP_DIR/VLMulti.Server"
# Gói được nén trên Windows nên mất quyền chạy của node (Playwright dùng để cài/chạy Chromium) → cấp lại
find "$APP_DIR/.playwright" -type f \( -name node -o -name '*.sh' \) -exec chmod +x {} + 2>/dev/null || true

echo "== Cài thư viện hệ thống, font emoji và Chromium"
apt-get update -y
apt-get install -y fonts-noto-color-emoji fonts-dejavu-core
"$APP_DIR/VLMulti.Server" --install-browser --with-deps

chown -R vlmulti:vlmulti "$APP_DIR" /var/lib/vlmulti

echo "== Mật khẩu trang web"
if [ ! -f "$DATA_DIR/server.json" ] || ! grep -q PasswordHash "$DATA_DIR/server.json" || grep -q '"PasswordHash": null' "$DATA_DIR/server.json"; then
  read -r -s -p "Đặt mật khẩu trang web điều khiển: " PW; echo
  sudo -u vlmulti PLAYWRIGHT_BROWSERS_PATH="$PLAYWRIGHT_BROWSERS_PATH" "$APP_DIR/VLMulti.Server" --set-password "$PW" --data "$DATA_DIR"
fi

echo "== Tạo dịch vụ chạy 24/7 (tự chạy lại khi VPS khởi động lại)"
cp "$APP_DIR/deploy/vlmulti.service" /etc/systemd/system/vlmulti.service
systemctl daemon-reload
systemctl enable --now vlmulti

sleep 3
systemctl --no-pager --lines=5 status vlmulti || true
echo
echo "Xong. Mở trình duyệt: http://<IP máy chủ>:8080  (nên dùng qua Tailscale, xem README)"
echo "Xem log:   journalctl -u vlmulti -f      hoặc   tail -f $DATA_DIR/vlmulti.log"
