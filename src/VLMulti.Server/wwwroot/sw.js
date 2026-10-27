// Service worker tối giản: cần có để Chrome Android cho "Cài đặt ứng dụng" (biểu tượng trên màn hình chính).
// Không lưu đệm gì: trang điều khiển luôn lấy dữ liệu mới từ server; mất mạng thì báo đơn giản.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));
self.addEventListener('fetch', (e) => {
  if (e.request.method !== 'GET' || new URL(e.request.url).pathname.startsWith('/api/')) return;
  e.respondWith(fetch(e.request).catch(() => new Response(
    '<meta charset="utf-8"><body style="background:#1e1e1e;color:#ddd;font:16px sans-serif;padding:24px">' +
    'Không kết nối được tới server VLMulti. Kiểm tra Tailscale / mạng rồi mở lại.</body>',
    { headers: { 'Content-Type': 'text/html; charset=utf-8' } })));
});
