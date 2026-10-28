// Kiểm tra nhanh sau khi cài bản mới lên server (chỉ đọc): đọc window.__vlDebug() của từng acc qua cổng DevTools
// và báo số liệu nào app chưa hiểu. Cần cổng DevTools của server (VLMULTI_DEBUG_PORT) mở qua đường hầm SSH, ví dụ:
//   ssh -i <key> -N -L 9555:127.0.0.1:9333 ubuntu@<server>
//   node tests/agent/smoke.mjs 9555
const port = process.argv[2] || '9555';
const targets = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).filter((t) => t.type === 'page');
let bad = 0;
for (const t of targets) {
  const ws = new WebSocket(t.webSocketDebuggerUrl);
  await new Promise((r) => (ws.onopen = r));
  let id = 0;
  const call = (expression) => new Promise((res) => {
    const my = ++id;
    ws.addEventListener('message', function h(ev) { const m = JSON.parse(ev.data); if (m.id === my) { ws.removeEventListener('message', h); res(m.result?.result?.value); } });
    ws.send(JSON.stringify({ id: my, method: 'Runtime.evaluate', params: { expression, returnByValue: true } }));
  });
  const acc = await call('window.__vlAccount');
  const d = await call('typeof window.__vlDebug === "function" ? window.__vlDebug() : null');
  const zone = await call('document.querySelector("#u20zn")?.textContent || ""');
  ws.close();
  if (!d) { console.log(`✗ ${acc}: chưa có bản mới (không thấy __vlDebug) – trang chưa tải lại?`); bad++; continue; }
  const q = d.quests;
  const checks = [
    ['KNB', d.knb && d.knb.knb >= 0 ? `${d.knb.knb} · khóa ${d.knb.khoa}` : ''],
    ['Dã Tẩu', q.dt], ['Lệnh bài đã dùng', q.ldtUsed], ['Phó bản', q.pb], ['Thuyền', q.boat],
    ['Boss tuần', q.wb], ['Sát Thủ', q.st], ['Hòm đồ', d.bag],
  ];
  const missing = checks.filter(([, v]) => !v).map(([k]) => k);
  const line = checks.map(([k, v]) => `${k} ${v || '?'}`).join(' · ');
  console.log(`${missing.length ? '✗' : '✓'} ${acc} [${zone}] ${line}` +
    `${d.unreadable.length ? ` · KHÔNG ĐỌC ĐƯỢC: ${d.unreadable.join(', ')}` : ''}${d.maintenance ? ' · BẢO TRÌ' : ''}${d.away ? ' · NƠI KHÁC' : ''}` +
    `${d.flowBusy ? ' · đang mua' : ''} · thẻ Hệ thống ${d.sysActive ? 'có' : 'không'}`);
  if (missing.length || d.unreadable.length) bad++;
}
console.log(bad ? `\n${bad} acc có số liệu chưa đọc được (trang vừa tải cần ~1–2 phút để đọc đủ)` : '\nTất cả acc đọc đủ số liệu');
process.exit(bad ? 1 : 0);
