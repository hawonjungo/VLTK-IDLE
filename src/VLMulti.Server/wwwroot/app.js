// Trang điều khiển VLMulti.Server – bố cục A cho điện thoại: danh sách acc gọn (chạm để mở chi tiết),
// thanh điều hướng dưới đáy (Tổng quan · Bảng · Nhật ký · Cài đặt).
// Dữ liệu từ game luôn được gán bằng textContent (không innerHTML).
'use strict';

const $ = (s) => document.querySelector(s);
let state = null;
let refreshTimer = null;
let lastOkAt = 0;

// Tạo phần tử: h('div', { class: 'x', onclick: f }, [con…])
function h(tag, props = {}, children = []) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of [].concat(children)) if (c != null) el.append(c);
  return el;
}
// Biểu tượng nét mảnh (SVG) dùng trong JS
function icon(path, size = 20) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  for (const [k, v] of Object.entries({ width: size, height: size, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': 2, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(k, v);
  const p = document.createElementNS(ns, 'path');
  p.setAttribute('d', path);
  svg.append(p);
  return svg;
}
const ICON_LIVE = 'M2 5h20v14H2zM10 9l5 3-5 3z';
const ICON_PLAY = 'M7 4l13 8-13 8z';
const ICON_MORE = 'M5 12h.01M12 12h.01M19 12h.01';

async function api(path, body) {
  const res = await fetch('/api/' + path, body === undefined ? {} : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
  if (res.status === 401 && path !== 'login') { showLogin(); throw new Error('unauthorized'); }
  const data = res.headers.get('content-type')?.includes('json') ? await res.json() : null;
  if (!res.ok) throw new Error(data?.error || `Lỗi ${res.status}`);
  return data;
}

function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.remove('hidden');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => t.classList.add('hidden'), 4000);
}

async function act(path, body, okMsg) {
  try {
    await api(path, body ?? {});
    if (okMsg) toast(okMsg);
    refresh();
  } catch (e) {
    if (e.message !== 'unauthorized') toast(e.message);
  }
}

// Lưu tuỳ chọn riêng của trình duyệt này (màn đang xem, acc đang mở, bật kêu…); trình duyệt chặn lưu thì thôi
const prefs = {
  get(k, d) { try { const v = localStorage.getItem('vl.' + k); return v == null ? d : JSON.parse(v); } catch (_) { return d; } },
  set(k, v) { try { localStorage.setItem('vl.' + k, JSON.stringify(v)); } catch (_) {} },
};

const fmt = (v) => (v == null || v === '' ? '–' : String(v));
const time = (s) => (s ? new Date(s).toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }) : '');
const ago = (sec) => (sec == null ? '' : sec < 60 ? `${sec} giây trước` : sec < 3600 ? `${Math.floor(sec / 60)} phút trước` : `${Math.floor(sec / 3600)} giờ trước`);
// Số lớn viết gọn: 17200 → "17,2k"
const short = (n) => (n == null ? '–' : n >= 10000 ? `${(n / 1000).toLocaleString('vi-VN', { maximumFractionDigits: 1 })}k` : n.toLocaleString('vi-VN'));
const nameOf = (id) => state?.accounts.find((x) => x.id === id)?.name || id;

// ---------- Đăng nhập ----------

function showLogin() {
  clearInterval(refreshTimer);
  refreshTimer = null;
  // Đang dùng mà bị đẩy ra (phiên hết hạn, server đổi khoá…) → đóng Live và nói rõ lý do
  if (!$('#main').classList.contains('hidden')) $('#loginError').textContent = 'Phiên đăng nhập trang web đã hết, hãy đăng nhập lại';
  closeLive();
  $('#main').classList.add('hidden');
  $('#login').classList.remove('hidden');
  $('#loginPassword').focus();
}

$('#loginForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  $('#loginError').textContent = '';
  try {
    await api('login', { password: $('#loginPassword').value });
    $('#loginPassword').value = '';
    start();
  } catch (err) {
    $('#loginError').textContent = err.message;
  }
});

function start() {
  $('#login').classList.add('hidden');
  $('#main').classList.remove('hidden');
  showView(prefs.get('view', 'home'));
  refresh();
  clearInterval(refreshTimer);
  refreshTimer = setInterval(refresh, 3000);
}

async function refresh() {
  try {
    state = await api('state');
    lastOkAt = Date.now();
    $('#connBanner').classList.add('hidden');
    render();
    checkNewAlerts();
  } catch (e) {
    if (e.message === 'unauthorized') return;
    const banner = $('#connBanner');
    banner.textContent = `Mất kết nối tới server – đang thử lại…${lastOkAt ? ` (cập nhật cuối ${time(new Date(lastOkAt))})` : ''}`;
    banner.classList.remove('hidden');
  }
}

// ---------- Chuyển màn (thanh dưới đáy) ----------

let view = 'home';
function showView(v) {
  if (!['home', 'table', 'log', 'settings'].includes(v)) v = 'home';
  view = v;
  prefs.set('view', v);
  for (const s of document.querySelectorAll('.view')) s.classList.toggle('hidden', s.id !== `view-${v}`);
  for (const b of document.querySelectorAll('.bottom-nav [data-view]')) b.classList.toggle('on', b.dataset.view === v);
  if (v !== 'home') setSelecting(false);
  if (v === 'log') loadLog();
  if (v === 'settings' && state) renderSettings();
  if (v === 'table' && state) renderTable();
  if (v === 'table') renderGuildKnb();
  window.scrollTo(0, 0);
}
for (const b of document.querySelectorAll('.bottom-nav [data-view]')) b.addEventListener('click', () => showView(b.dataset.view));

function render() {
  const alerts = state.alerts.length;
  const badge = $('#alertsBadge');
  badge.textContent = alerts > 99 ? '99+' : String(alerts);
  badge.classList.toggle('hidden', !alerts);
  $('#alertsBtn').setAttribute('aria-label', alerts ? `Cảnh báo (${alerts})` : 'Cảnh báo');
  renderHome();
  if (view === 'table') renderTable();
}

// ---------- Tổng quan: ô tổng + danh sách acc gọn ----------

let openId = prefs.get('open', null);
let selecting = false;
const moreOpen = new Set();
// Thẻ nào đang mở rộng phần nhật ký (danh sách dựng lại mỗi 3 giây nên phải nhớ)
const logOpenIds = new Set();

function renderHome() {
  const accs = state.accounts.filter((a) => a.enabled);
  const sum = (k) => accs.reduce((n, a) => n + (a[k] || 0), 0);
  const gold = accs.reduce((n, a) => n + (a.goldValue || 0), 0);
  const doPho = accs.reduce((n, a) => n + (a.doPho || []).length, 0);
  // sub: dòng nhỏ thứ hai (KNB khóa nằm dưới KNB, không nhét chung một dòng – ô hẹp bị cắt thành "16,1k · 5…")
  const tile = (k, v, cls, sub) => h('div', { class: 'tile' }, [h('div', { class: 'k', text: k }),
    h('div', { class: 'v' + (cls ? ' ' + cls : ''), text: v }), sub ? h('div', { class: 'sub', text: sub }) : null]);
  $('#totals').replaceChildren(
    tile('Ngân lượng', `${(gold / 10000).toLocaleString('vi-VN', { maximumFractionDigits: 0 })} vạn`),
    tile('KNB', short(sum('knb')), null, `🔒 ${short(sum('knbLocked'))} khóa`),
    tile('Đồ phổ', String(doPho), doPho ? 'gold' : null),
  );
  $('#updated').textContent = `${accs.length} acc · cập nhật ${ago(Math.round((Date.now() - lastOkAt) / 1000)) || 'vừa xong'}`;

  // Server đang tạm dừng (nhường acc cho máy khác): dải thông báo + nút chạy lại
  $('#pauseBar').replaceChildren(...(state.paused ? [
    h('div', {}, [h('b', { text: '⏸ Server đang tạm dừng' }), h('div', { class: 'small-note', text: 'Các acc đã thoát game, bạn chơi ở máy khác thoải mái.' })]),
    h('button', { class: 'btn primary small', text: 'Chạy lại', onclick: () => act('pause', { on: false }, 'Đang mở lại các acc…') }),
  ] : []));
  $('#pauseBar').classList.toggle('hidden', !state.paused);

  const list = $('#accList');
  const seen = new Set();
  for (const a of state.accounts) {
    seen.add(a.id);
    let el = list.querySelector(`[data-id="${a.id}"]`);
    const card = a.playingSince ? playCard(a) : a.enabled ? accCard(a) : offCard(a);
    if (el) el.replaceWith(card); else list.append(card);
  }
  for (const el of [...list.children]) if (!seen.has(el.dataset.id)) el.remove();
  renderSelectBar();
}

// Bước đang chạy để hiện trên dòng gọn: bước "current" của vòng tự động, hoặc chế độ đang chạy
function stepOf(a) {
  if (a.maintenance) return { label: 'Bảo trì', auto: false, maint: true };
  const cur = a.autoChain?.find((s) => s.state === 'current');
  if (cur) return { label: cur.label, auto: true };
  return { label: a.mode ? a.mode.replace(/^\S+\s/, '') : (a.screen === 'game' ? '—' : a.screenLabel), auto: false };
}

function subOf(a) {
  if (a.maintenance) return '🛠 Máy chủ game đang bảo trì – app tạm đứng yên';
  if (a.health !== 'good' && a.healthReason) return a.healthReason;
  const parts = [a.sect, a.tower != null ? `tầng ${a.tower}` : null,
    a.screen === 'game' && a.idleSeconds != null ? `tiến triển ${ago(a.idleSeconds)}` : null];
  return parts.filter(Boolean).join(' · ') || a.screenLabel;
}

function accCard(a) {
  const open = !selecting && openId === a.id;
  const step = stepOf(a);
  const row = h('button', {
    class: 'acc-row', 'aria-expanded': open ? 'true' : 'false',
    onclick: () => {
      if (selecting) { act(`acc/${a.id}/select`, { on: !a.selected }); return; }
      openId = openId === a.id ? null : a.id;
      prefs.set('open', openId);
      renderHome();
    },
  }, [
    selecting ? h('span', { class: 'check' + (a.selected ? ' on' : ''), 'aria-hidden': 'true' }) : null,
    h('span', { class: `dot ${a.health || ''}`, 'aria-hidden': 'true' }),
    h('span', { class: 'acc-main' }, [
      h('span', { class: 'acc-name' }, [h('b', { text: a.name }), a.level ? h('small', { text: `Lv ${a.level}` }) : null]),
      h('span', { class: `acc-sub ${a.health === 'good' ? '' : a.health}`, text: subOf(a) }),
    ]),
    h('span', { class: 'acc-side' }, [
      h('span', { class: 'step-pill' + (step.maint ? ' maint' : step.auto ? '' : ' idle'), text: step.label }),
      h('span', { class: 'rate', text: a.ratePerHour ? `${a.ratePerHour}/giờ` : '—' }),
    ]),
  ]);
  // Nhật ký ngay trên thẻ: 3 dòng mới nhất; bấm vào thì mở rộng (nhiều dòng, xuống dòng đủ chữ), bấm lần nữa thu lại
  const logOpen = logOpenIds.has(a.id);
  const log = a.activity?.length
    ? h('div', { class: 'mini-log card-log' + (logOpen ? ' open' : ''), title: logOpen ? 'Bấm để thu gọn' : 'Bấm để xem thêm',
        onclick: () => { if (logOpen) logOpenIds.delete(a.id); else logOpenIds.add(a.id); renderHome(); } }, [
        ...a.activity.slice(0, logOpen ? 15 : 3).map((e) => h('div', {}, [h('time', { text: time(e.at) }), e.text])),
        a.activity.length > 3 ? h('div', { class: 'more-hint', text: logOpen ? 'Thu gọn ▴' : `Xem thêm ${Math.min(15, a.activity.length) - 3} dòng ▾` }) : null,
      ])
    : null;
  return h('article', { class: `acc${open ? ' open' : ''}${a.health === 'bad' ? ' bad' : ''}`, 'data-id': a.id },
    [row, selecting ? null : log, open ? accDetail(a) : null]);
}

function accDetail(a) {
  const tile = (k, v, cls) => h('div', { class: 'tile' }, [h('div', { class: 'k', text: k }), h('div', { class: 'v' + (cls ? ' ' + cls : ''), text: fmt(v) })]);
  const doPho = a.doPho || [];
  const stats = h('div', { class: 'stats' }, [
    tile('Tầng tháp', a.tower),
    tile('Tầng/giờ', a.floorsPerHour),
    tile('Vạn/giờ', a.ratePerHour),
    tile('Hòm đồ', a.bagUsed != null ? `${a.bagUsed}/${a.bagMax}` : null, a.bagFull ? 'warn' : null),
    tile('KNB · khóa', a.knb != null ? `${short(a.knb)} · ${short(a.knbLocked ?? 0)}` : null),
    tile('Đồ phổ', doPho.length, doPho.length ? 'gold' : null),
  ]);

  // Việc hôm nay (vòng tự động): mỗi bước một ô ✅ xong / ▶ đang làm / 💤 tạm nghỉ / ⬜ chưa làm
  let chain = null;
  if (a.autoChain) {
    const icon = { done: '✅', current: '▶', rest: '💤', off: '✕' };
    chain = h('div', { class: 'chain-box' }, [
      h('div', { class: 'small-note', text: `Việc hôm nay${a.autoCheckedAgo != null ? ` · xét lại ${ago(a.autoCheckedAgo)}` : ''}` }),
      h('div', { class: 'todo-grid' }, a.autoChain.map((s) => h('div', { class: `todo ${s.state}` }, [
        h('i', { text: icon[s.state] || '⬜' }), h('span', { text: s.label }), s.detail ? h('small', { text: s.detail }) : null,
      ]))),
    ]);
  }

  const dopho = doPho.length
    ? h('div', { class: 'dopho-box' }, [h('b', { text: `Đồ phổ đang giữ: ${doPho.length}` }), ...doPho.map((n) => h('div', { text: n }))])
    : null;

  // Số liệu phụ gom vào "Thêm"
  const chip = (k, v) => (v == null || v === '' ? null : h('span', { class: 'chip' }, [`${k} `, h('b', { text: String(v) })]));
  // Danh sách dựng lại mỗi 3 giây → nhớ mục "Thêm số liệu" đang mở của acc nào
  const more = h('details', { class: 'more', open: moreOpen.has(a.id), ontoggle: (e) => {
    if (e.target.open) moreOpen.add(a.id); else moreOpen.delete(a.id);
  } }, [
    h('summary', { text: 'Thêm số liệu' }),
    h('div', { class: 'chips' }, [
      chip('Ngân lượng', a.gold),
      chip('Dã Tẩu', a.daTau ? (a.daTauMax ? `${a.daTau} · tối đa ${a.daTauMax}` : a.daTau) : null),
      chip('Lệnh bài', a.ldtHave), chip('LB đã dùng', a.ldtUsed), chip('Mua LB', a.ldtBuy),
      chip('Phó bản', a.phoBan), chip('Thuyền', a.boat), chip('Boss tuần', a.weekBoss),
      chip('LB Sát Thủ', a.assassin ? a.assassin.split('/')[0] : null),
      chip('Kinh nghiệm', a.exp), chip('Ra khỏi tháp', a.towerFalls || null),
      chip('Lên cấp', a.levelsGained > 0 ? `+${a.levelsGained}` : null), chip('Chế độ', a.mode),
      chip('Trạng thái', a.status),
      a.tracker ? chip('Nhiệm vụ', a.tracker) : null,
      a.lastDoPho ? chip('Đồ phổ mới nhặt', `${a.lastDoPho} (${time(a.lastDoPhoAt)})`) : null,
    ].filter(Boolean)),
  ]);

  const actions = h('div', { class: 'acc-actions' }, [
    h('button', { class: 'btn primary', title: 'Mở game trên trình duyệt này để tự chơi; đóng tab game thì acc tự về server',
      onclick: () => startPlay(a) }, [icon(ICON_PLAY), 'Chơi']),
    h('button', { class: 'btn', title: 'Xem nhanh màn hình acc trên server (acc vẫn chạy tự động)',
      onclick: () => openLive(a.id, a.name) }, [icon(ICON_LIVE), a.live ? 'Xem •' : 'Xem']),
    h('button', { class: 'icon-btn', 'aria-label': 'Thêm thao tác', onclick: () => openAccMenu(a.id) }, [icon(ICON_MORE)]),
  ]);
  return h('div', { class: 'acc-detail' }, [stats, chain, dopho, more, actions]);
}

function offCard(a) {
  return h('article', { class: 'acc off', 'data-id': a.id }, [
    h('div', { class: 'acc-row' }, [
      h('span', { class: 'dot', 'aria-hidden': 'true' }),
      h('span', { class: 'acc-main' }, [h('span', { class: 'acc-name' }, [h('b', { text: a.name })]),
        h('span', { class: 'acc-sub', text: state.paused ? 'Đang tạm dừng' : 'Đã tắt' })]),
      state.paused ? null : h('button', { class: 'btn small', text: 'Bật lại', onclick: () => act(`acc/${a.id}/enabled`, { on: true }, 'Đang mở lại acc…') }),
    ]),
  ]);
}

// ---------- ▶ Chơi: tự chơi acc trên trình duyệt này ----------
// Server nhả acc (thoát game của acc) rồi mở game ở tab mới. Trang này để ý tab đó: đóng tab → báo server nhận lại acc,
// đăng nhập và chạy tiếp. Trang game là trang khác nên chỉ biết được tab đã đóng hay chưa, không đọc được gì bên trong.
const playTabs = new Map(); // id → { w: tab game đang mở từ trang này, at: lúc mở }
// Trang này mở dạng ứng dụng (thêm vào màn hình chính): tab game mở ra trình duyệt khác và trình duyệt báo "đã đóng" ngay
// → không theo dõi được. Tab "đóng" trong vài giây đầu = không theo dõi được, KHÔNG tự trả acc (bạn bấm Trả lại server).
const PLAY_TRUST_AFTER_MS = 15000;

async function startPlay(a) {
  // Mở tab ngay trong lúc bấm (mở sau khi chờ server thì trình duyệt chặn cửa sổ bật lên), rồi mới chuyển tới game
  const w = window.open('', `vlplay-${a.id}`);
  if (!w) { toast('Trình duyệt chặn mở tab mới – cho phép cửa sổ bật lên của trang này rồi bấm lại'); return; }
  try { w.document.title = `${a.name} – đang mở…`; w.document.body.textContent = 'Đang nhả acc trên server, chờ chút…'; } catch (_) {}
  try {
    await api(`acc/${a.id}/play`, {});
  } catch (e) {
    w.close();
    if (e.message !== 'unauthorized') toast(e.message);
    return;
  }
  w.location.href = state.gameUrl || 'https://volamidle.pages.dev/';
  playTabs.set(a.id, { w, at: Date.now() });
  toast(`${a.name}: server đã nhả acc – đăng nhập ở tab mới. Đóng tab game là acc tự về server.`);
  refresh();
}

// Mở lại tab game (ví dụ trang này đã tải lại nên không còn theo dõi được tab cũ). Cùng tên tab → tab cũ còn mở thì dùng lại.
function reopenPlay(a) {
  const w = window.open(state.gameUrl || 'https://volamidle.pages.dev/', `vlplay-${a.id}`);
  if (!w) { toast('Trình duyệt chặn mở tab mới – cho phép cửa sổ bật lên của trang này rồi bấm lại'); return; }
  playTabs.set(a.id, { w, at: Date.now() });
}

async function returnPlay(id) {
  playTabs.delete(id);
  const a = state?.accounts.find((x) => x.id === id);
  await act(`acc/${id}/return`, {}, `${a ? a.name : id}: đã trả acc cho server – đang đăng nhập lại`);
}

function checkPlayTabs() {
  for (const [id, t] of playTabs) {
    if (!t.w.closed) continue;
    if (Date.now() - t.at < PLAY_TRUST_AFTER_MS) {
      playTabs.delete(id);
      const a = state?.accounts.find((x) => x.id === id);
      toast(`${a ? a.name : id}: không theo dõi được tab game (trang này đang mở dạng ứng dụng?) – chơi xong bấm "Trả lại server"`);
      renderHome();
      continue;
    }
    returnPlay(id);
  }
}
setInterval(checkPlayTabs, 2000);
// Điện thoại cho trang này "ngủ" khi ở nền: quay lại là xét ngay
document.addEventListener('visibilitychange', () => { if (!document.hidden) checkPlayTabs(); });

function playCard(a) {
  const mins = Math.max(0, Math.floor((Date.now() - new Date(a.playingSince)) / 60000));
  const watching = playTabs.has(a.id);
  return h('article', { class: 'acc off playing', 'data-id': a.id }, [
    h('div', { class: 'acc-row' }, [
      h('span', { class: 'dot warn', 'aria-hidden': 'true' }),
      h('span', { class: 'acc-main' }, [h('span', { class: 'acc-name' }, [h('b', { text: a.name })]),
        h('span', { class: 'acc-sub', text: `🎮 Bạn đang chơi · ${mins < 1 ? 'vừa bắt đầu' : `${mins} phút`}${watching ? ' · đóng tab game là tự về server' : ''}` })]),
    ]),
    h('div', { class: 'acc-actions play-actions' }, [
      watching ? null : h('button', { class: 'btn', text: 'Mở lại game', title: 'Mở tab game (trang này đã tải lại nên không còn theo dõi tab cũ)', onclick: () => reopenPlay(a) }),
      h('button', { class: 'btn primary', text: 'Trả lại server', onclick: () => {
        const w = playTabs.get(a.id)?.w;
        if (w && !w.closed && !confirm(`Trả ${a.name} cho server? Tab game đang mở sẽ bị đẩy ra khi server đăng nhập lại.`)) return;
        returnPlay(a.id);
      } }),
    ]),
  ]);
}

// Nút "⋯": tấm thao tác cho một acc
function openAccMenu(id) {
  const a = state.accounts.find((x) => x.id === id);
  if (!a) return;
  const item = (label, note, onclick, cls) => h('button', { class: 'btn' + (cls ? ' ' + cls : ''), onclick }, [label, note ? h('small', { text: note }) : null]);
  openModal(a.name, h('div', { class: 'sheet-list' }, [
    item('Ảnh nhanh', '', () => { closeModal(); openSnapshot(a.id, a.name); }),
    item('Nhật ký đầy đủ', '', () => { closeModal(); logAcc = a.id; showView('log'); }),
    item('Kéo quái khi leo tháp', (a.kite ? 'đang BẬT' : 'đang tắt') + (a.kiteDir || a.kiteDist || a.kiteInterval || a.kiteFlee != null || a.kiteCrowd != null || a.kitePause ? ' · cài riêng' : ' · theo cài đặt chung'), () => { closeModal(); openKiteSheet(a); }),
    item('Vòng tự động (luật)', a.autoMode ? 'đang BẬT' : 'đang tắt', () => { closeModal(); act(`acc/${a.id}/automode`); }),
    item('Tải lại trang game', '', () => { closeModal(); act(`acc/${a.id}/reload`, {}, 'Đang tải lại…'); }),
    item('Tắt acc', 'đóng game, giải phóng RAM/CPU', () => {
      if (confirm(`Tắt ${a.name}? Game của acc này sẽ đóng cho tới khi bật lại.`)) { closeModal(); act(`acc/${a.id}/enabled`, { on: false }); }
    }, 'danger'),
  ]));
}

// ---------- 🐴 Mã Trường: quét → xem danh sách chuyển → bấm Chuyển ----------
// Quét: mỗi acc trong game nhận thư ngựa còn chờ, mở Rương Ngựa (tuỳ chọn), cất loài mới vào Mã Trường, đọc bộ sưu tập.
// Chuyển chỉ chạy khi bạn bấm, chỉ giữa các nhân vật của bạn, thư tặng 0 KNB.
let horseOpen = false, horseTimer = 0, horseChests = prefs.get('horseChests', true), horseError = '';
async function openHorses() {
  horseOpen = true;
  await renderHorses();
}
async function renderHorses() {
  clearTimeout(horseTimer);
  if (!horseOpen) return;
  let st;
  try { st = await api('horses'); } catch (e) { if (e.message !== 'unauthorized') toast(e.message); return; }
  // Bạn đã mở hộp thoại khác trong lúc chờ → thôi cập nhật
  if (!horseOpen || (!$('#modal').classList.contains('hidden') && $('#modalTitle').textContent !== '🐴 Mã Trường')) { horseOpen = false; return; }
  const busy =st.phase === 'scanning' || st.phase === 'sending';
  const parts = [];
  if (st.phase === 'idle' || st.phase === 'done') {
    parts.push(h('p', { class: 'dim', text: 'Nhận thư ngựa còn chờ, cất loài mới vào Mã Trường, rồi tìm ngựa thừa ở acc này mà acc khác còn thiếu. Chưa gửi gì cho tới khi bạn bấm Chuyển.' }));
    const cb = h('input', { type: 'checkbox', onchange: (e) => { horseChests = e.target.checked; prefs.set('horseChests', horseChests); } });
    cb.checked = horseChests;
    parts.push(h('label', { class: 'check-row' }, [cb, 'Mở hết Rương Ngựa trước']));
  }
  // Lỗi lần bấm gần nhất: hiện ngay trong bảng (thông báo nhỏ dễ bị che, trông như "bấm mà không có gì")
  if (horseError && !busy) parts.push(h('p', { class: 'warn', text: '⚠ ' + horseError }));
  if (busy) parts.push(h('p', { text: st.phase === 'scanning' ? '⏳ Đang quét các acc…' : '⏳ Đang chuyển ngựa…' }));
  if (st.lines?.length) parts.push(h('div', { class: 'horse-lines' }, st.lines.map((l) => h('div', { text: l }))));
  const nSell = (st.sales || []).reduce((n, x) => n + x.count, 0);
  if (st.phase === 'ready' && st.plan?.length) {
    parts.push(h('h3', { text: `Danh sách chuyển (${st.plan.length} con)` }));
    parts.push(h('div', { class: 'horse-plan' }, st.plan.map((p) => h('div', {
      text: `${p.from} → ${p.to}: ${p.horse}` + (p.locked ? ' · 🔒 sẽ mở khoá' : '') + (p.inStable ? ' · lấy ra từ Mã Trường' : ''),
    }))));
  }
  if (st.phase === 'ready' && nSell) {
    parts.push(h('h3', { text: `Bán ngựa thừa không acc nào cần (${nSell} con, không bán con đang khoá)` }));
    parts.push(h('div', { class: 'horse-plan' }, st.sales.map((x) => h('div', { text: `${x.name}: ${x.horses}` }))));
  }
  const actions = [];
  if (st.phase === 'ready') {
    const what = [st.plan.length ? `Chuyển ${st.plan.length} con` : '', nSell ? `Bán ${nSell} con` : ''].filter(Boolean).join(' · ');
    actions.push(h('button', { class: 'btn primary', text: what, onclick: async () => {
      if (!confirm(`${what}?

Chuyển: thư tặng (0 KNB) giữa các nhân vật của bạn. Bán: ngựa thừa mà không acc nào còn thiếu loài đó.`)) return;
      horseError = '';
      try { await api('horses/send', {}); } catch (e) { horseError = e.message; toast(e.message); }
      renderHorses();
    } }));
    actions.push(h('button', { class: 'btn', text: 'Bỏ', onclick: async () => { try { await api('horses/cancel', {}); } catch (_) {} renderHorses(); } }));
  } else if (!busy) {
    actions.push(h('button', { class: 'btn primary', text: st.phase === 'done' ? 'Quét lại' : 'Quét', onclick: async () => {
      horseError = '';
      try { await api('horses/scan', { on: horseChests }); } catch (e) { horseError = e.message; toast(e.message); }
      renderHorses();
    } }));
  }
  if (actions.length) parts.push(h('div', { class: 'horse-actions' }, actions));
  openModal('🐴 Mã Trường', parts);
  if (busy) horseTimer = setTimeout(renderHorses, 2000);
}
$('#horseBtn').addEventListener('click', openHorses);

// ---------- 📜 Nhật ký game ----------
// Mỗi dòng game ghi về chính nhân vật (kèm tầng / bản đồ), lọc theo nhân vật, loại, ngày; "💎 Đồ hiếm" gộp các nhân vật của bạn
const GAMELOG_KINDS = [['', 'Mọi loại'], ['rare', '💎 Đồ hiếm'], ['boss', '👑 Boss'],
  ['assassin', '🗡 Sát Thủ'], ['tower', '🗼 Tháp'], ['death', '💀 Gục ngã'], ['loot', '🎁 Nhặt / nhận đồ'], ['other', 'Khác']];
const vnToday = () => new Date(Date.now() + 7 * 3600e3).toISOString().slice(0, 10);
let logSel = { who: 'rare', kind: '', day: '' };
async function openGameLog() {
  const accs = (state?.accounts || []);
  const opt = (v, t, cur) => { const o = h('option', { value: v, text: t }); o.selected = v === cur; return o; };
  const who = h('select', {}, [opt('rare', '💎 Đồ hiếm – các nhân vật của bạn', logSel.who)].concat(accs.map((a) => opt(a.id, a.name || a.id, logSel.who))));
  const kind = h('select', {}, GAMELOG_KINDS.map(([v, t]) => opt(v, t, logSel.kind)));
  const day = h('input', { type: 'date', value: logSel.day || vnToday() });
  const list = h('div', { class: 'gamelog' }, [h('p', { class: 'dim', text: 'Đang tải…' })]);
  const load = async () => {
    if (!who.value) who.value = 'rare'; // nhân vật không còn trong danh sách → về Đồ hiếm
    if (!day.value) day.value = vnToday();
    logSel = { who: who.value, kind: kind.value, day: day.value };
    kind.disabled = who.value === 'rare';
    list.replaceChildren(h('p', { class: 'dim', text: 'Đang tải…' }));
    try {
      const rows = who.value === 'rare'
        ? await api(`gamelog/rare?day=${day.value}`)
        : await api(`acc/${encodeURIComponent(who.value)}/gamelog?day=${day.value}${kind.value ? '&kinds=' + kind.value : ''}`);
      list.replaceChildren(...(rows.length ? rows.map((r) => h('div', { class: 'gl-row gl-' + r.kind }, [
        h('span', { class: 'gl-at', text: r.at }),
        h('span', { class: 'gl-place', text: r.place || '' }),
        who.value === 'rare' ? h('span', { class: 'gl-char', text: r.char || '' }) : null,
        h('span', { class: 'gl-text', text: r.text }),
      ].filter(Boolean))) : [h('p', { class: 'dim', text: 'Chưa có dòng nào.' })]));
    } catch (e) { list.replaceChildren(h('p', { class: 'warn', text: '⚠ ' + e.message })); }
  };
  for (const el of [who, kind, day]) el.addEventListener('change', load);
  openModal('📜 Log game', [
    h('div', { class: 'form-grid gamelog-filter' }, [h('label', { text: 'Nhân vật' }), who, h('label', { text: 'Loại' }), kind, h('label', { text: 'Ngày' }), day]),
    list,
  ], true);
  load();
}
$('#gameLogBtn').addEventListener('click', openGameLog);

// ---------- 🏯 Sổ KNB của bang (dưới "Bảng chi tiết") ----------
// Các acc trong bang đọc Nhật ký bang ~3 phút một lần; bảng tuần thứ Hai → Chủ Nhật: KNB từng ngày, tổng / mức tuần,
// trạng thái theo tiến độ (mức mỗi ngày × số ngày đã qua), xuất CSV, lọc người chưa đủ.
let gkWeek = '', gkOnlyShort = false, gkTimer = 0;
const gkN = (n) => Number(n || 0).toLocaleString('vi-VN');
const CHUC = { chu: 'Bang chủ', pho: 'Phó', lao: 'Trưởng lão', chung: '' };
const addDays = (iso, n) => { const d = new Date(iso + 'T00:00:00Z'); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); };
const gkAgo = (ms) => { if (!ms) return ''; const m = Math.round((Date.now() - ms) / 60000); return m < 60 ? `${Math.max(0, m)} phút` : m < 1440 ? `${Math.round(m / 60)} giờ` : `${Math.round(m / 1440)} ngày`; };
async function renderGuildKnb() {
  clearTimeout(gkTimer);
  const box = $('#guildKnb');
  if (!box || $('#view-table').classList.contains('hidden')) return;
  let w;
  try { w = await api(`guild/knb?week=${gkWeek || vnToday()}`); } catch (e) { box.replaceChildren(h('p', { class: 'warn', text: '⚠ ' + e.message })); return; }
  gkWeek = w.week;
  const dd = (iso) => iso.slice(8, 10) + '/' + iso.slice(5, 7);
  const dows = ['T2', 'T3', 'T4', 'T5', 'T6', 'T7', 'CN'];
  const head = h('div', { class: 'gk-head' }, [
    h('h2', { class: 'view-title', text: '🏯 Bang · KNB tuần' + (w.guild ? ` – ${w.guild}` : '') }),
    h('div', { class: 'gk-nav' }, [
      h('button', { class: 'pill-btn', text: '‹', title: 'Tuần trước', onclick: () => { gkWeek = addDays(gkWeek, -7); renderGuildKnb(); } }),
      h('span', { class: 'gk-range', text: `${dd(w.days[0])} – ${dd(w.days[6])}` }),
      h('button', { class: 'pill-btn', text: '›', title: 'Tuần sau', onclick: () => { gkWeek = addDays(gkWeek, 7); renderGuildKnb(); } }),
    ]),
  ]);
  if (!w.guild) {
    box.replaceChildren(head, h('p', { class: 'dim', text: 'Chưa có dữ liệu: các acc trong bang đọc nhật ký bang 3 phút một lần sau khi vào game.' }));
    return;
  }
  const parts = [head];
  if (w.fund && w.fund.needKnb) {
    const pct = Math.min(100, Math.round(100 * w.fund.knb / w.fund.needKnb));
    parts.push(h('div', { class: 'gk-fund' }, [
      h('div', { text: `Quỹ KNB ${gkN(w.fund.knb)} / ${gkN(w.fund.needKnb)} để lên cấp ${(w.fund.cap || 0) + 1}` }),
      h('div', { class: 'gk-bar' }, [h('i', { style: `width:${pct}%` })]),
    ]));
  }
  const full = w.target * 7;
  const rows = w.members.filter((m) => !gkOnlyShort || m.status === 'short' || m.status === 'none');
  const order = { none: 0, short: 1, ok: 2, full: 3 };
  rows.sort((a, b) => order[a.status] - order[b.status] || b.total - a.total || a.name.localeCompare(b.name));
  const mark = { full: '✅', ok: '🟢', short: '⚠', none: '❌' };
  const cnt = (st) => w.members.filter((m) => m.status === st).length;
  parts.push(h('div', { class: 'gk-tools' }, [
    h('span', { class: 'dim', text: `Mức ${gkN(w.target)}/ngày · ${gkN(full)}/tuần · ✅ ${cnt('full')} 🟢 ${cnt('ok')} ⚠ ${cnt('short')} ❌ ${cnt('none')}` }),
    h('label', { class: 'check-row' }, [(() => { const c = h('input', { type: 'checkbox' }); c.checked = gkOnlyShort; c.onchange = () => { gkOnlyShort = c.checked; renderGuildKnb(); }; return c; })(), 'Chỉ người chưa đủ']),
    h('button', { class: 'pill-btn', text: '⬇ CSV', onclick: () => {
      const csv = [['Thành viên', 'Chức', 'Cấp', ...w.days.map(dd), 'Tổng KNB', 'Còn thiếu', 'Vạn tuần'].join(',')]
        .concat(w.members.map((m) => [`"${m.name.replace(/"/g, '""')}"`, CHUC[m.chuc] ?? m.chuc, m.lvl, ...m.days, m.total, m.owed, m.van].join(','))).join('\n');
      const a = h('a', { href: URL.createObjectURL(new Blob(['\ufeff' + csv], { type: 'text/csv' })), download: `knb-bang-${w.week}.csv` });
      a.click();
    } }),
  ]));
  const th = (t, cls) => h('th', { class: cls || '', text: t });
  const table = h('table', { class: 'gk-table' }, [
    h('thead', {}, [h('tr', {}, [th('Thành viên', 'gk-name'), ...w.days.map((d, i) => th(`${dows[i]} ${dd(d)}`)), th('Tổng'), th('')])]),
    h('tbody', {}, rows.map((m) => h('tr', { class: 'gk-' + m.status }, [
      h('td', { class: 'gk-name' }, [
        h('b', { text: m.name }),
        h('small', { class: 'dim', text: [CHUC[m.chuc] ?? m.chuc, m.lvl ? `cấp ${m.lvl}` : '', m.left ? 'đã rời bang' : '', m.on ? `on ${gkAgo(m.on)} trước` : ''].filter(Boolean).join(' · ') }),
      ]),
      ...m.days.map((n, i) => h('td', { class: (i < w.elapsed && n < w.target ? 'gk-miss ' : '') + 'gk-num', text: n ? gkN(n) : (i < w.elapsed ? '–' : '') })),
      h('td', { class: 'gk-num', text: `${gkN(m.total)}` }),
      h('td', { class: 'gk-st', title: m.owed ? `Còn thiếu ${gkN(m.owed)} KNB theo tiến độ` : '', text: mark[m.status] + (m.owed ? ` −${gkN(m.owed)}` : '') }),
    ]))),
  ]);
  parts.push(h('div', { class: 'table-wrap gk-wrap' }, [table]));
  if (w.gaps && w.gaps.length) {
    const t = (ms) => new Date(ms + 7 * 3600e3).toISOString().slice(11, 16) + ' ' + dd(new Date(ms + 7 * 3600e3).toISOString().slice(0, 10));
    parts.push(h('p', { class: 'note', text: '⚠ Có thể thiếu dòng (nhật ký bang trôi quá 30 dòng giữa 2 lần đọc): ' + w.gaps.map((g) => `${t(g.from)} → ${t(g.to)}`).join(', ') }));
  }
  parts.push(h('p', { class: 'dim small', text: `Đọc nhật ký bang lần cuối ${gkAgo(w.polledAt) || '—'} trước · ✅ đủ cả tuần · 🟢 đúng tiến độ · ⚠ còn thiếu · ❌ chưa góp` }));
  box.replaceChildren(...parts);
  gkTimer = setTimeout(renderGuildKnb, 60000);
}
$('#modalClose').addEventListener('click', () => { horseOpen = false; });
$('#modal').addEventListener('click', (e) => { if (e.target.id === 'modal') horseOpen = false; });

// ---------- 🏃 Kéo quái riêng từng acc ----------
// Mỗi ô có lựa chọn "Theo cài đặt chung" (giá trị rỗng = null ở server)
function openKiteSheet(a) {
  const s = state.settings || {};
  const sel = (opts, cur, commonLabel) => h('select', {}, [[ '', `Theo cài đặt chung (${commonLabel})` ]].concat(opts).map(([v, t]) => {
    const o = h('option', { value: v, text: t }); o.selected = String(cur ?? '') === String(v); return o;
  }));
  const dirs = (s.kiteDirections || []).map((d) => [d.id, d.label]);
  const dirLabel = (dirs.find(([v]) => v === s.kiteDirection) || [, s.kiteDirection])[1];
  const on = h('input', { type: 'checkbox' }); on.checked = !!a.kite;
  const dir = sel(dirs, a.kiteDir, dirLabel);
  const dist = sel([100, 120, 150, 180, 250, 350, 450, 600].map((n) => [n, `${n} px`]), a.kiteDist, `${s.kiteDistance} px`);
  const crowdOpts = [[0, 'Luôn chạy theo nhịp'], [2, 'Hơn 2 con'], [3, 'Hơn 3 con'], [4, 'Hơn 4 con'], [5, 'Hơn 5 con']];
  const crowdLabel = (n) => (crowdOpts.find(([v]) => v === n) || [, `hơn ${n} con`])[1];
  const crowd = sel(crowdOpts, a.kiteCrowd, crowdLabel(s.kiteCrowd));
  const pause = sel([300, 500, 800, 1000, 1500, 2000, 3000].map((n) => [n, `${n / 1000} giây`]), a.kitePause, `${(s.kitePause || 500) / 1000} giây`);
  const interval = sel([1, 2, 3, 4, 6, 8, 10, 15].map((n) => [n, `${n} giây`]), a.kiteInterval, `${s.kiteInterval} giây`);
  const flee = sel([[0, 'Không chạy']].concat([25, 30, 40, 50, 60].map((n) => [n, `< ${n}% máu`])), a.kiteFlee, s.kiteFleeHp ? `< ${s.kiteFleeHp}% máu` : 'không');
  const num = (el) => (el.value === '' ? null : +el.value);
  openModal(`🏃 Kéo quái · ${a.name}`, [
    h('p', { class: 'note', text: 'Chỉ chạy khi Leo Tháp và Auto đang bật. Kiểu "Hàng dọc": quái ở gần đông hơn mức chọn thì chạy bước ngắn lên xuống đường giữa phòng, mỗi bước dừng một chút để ra chiêu (vừa chạy vừa đánh), quái xếp thành hàng dọc sau lưng – chạy cả khi máu đầy; ít quái thì đứng đánh. Sang tầng mới, bị đẩy lệch hay sát mép / góc phòng thì chạy về giữa; né lúc quái vừa ra; máu thấp thì chạy ra xa.' }),
    h('label', { class: 'check-row' }, [on, 'Bật kéo quái cho acc này']),
    h('div', { class: 'form-grid' }, [
      h('label', { text: 'Kiểu kéo' }), dir,
      h('label', { text: 'Chạy khi quái ở gần' }), crowd,
      h('label', { text: 'Mỗi bước chạy' }), dist,
      h('label', { text: 'Dừng ra chiêu giữa 2 bước' }), pause,
      h('label', { text: 'Máu thấp thì chạy xa' }), flee,
      h('label', { text: 'Chu kỳ (kiểu cũ)' }), interval,
    ]),
    h('button', { class: 'btn primary', text: 'Lưu', onclick: async () => {
      await act(`acc/${a.id}/kite-config`, { on: on.checked, direction: dir.value || null, distance: num(dist), interval: num(interval), fleeHp: num(flee), crowd: num(crowd), pauseMs: num(pause) }, `Đã lưu kéo quái cho ${a.name}`);
      closeModal();
    } }),
  ]);
}

// ---------- Chọn nhiều acc → đổi chế độ ----------

function setSelecting(on) {
  selecting = on;
  $('#selectBtn').textContent = on ? 'Xong' : 'Chọn';
  $('#selectBtn').classList.toggle('on', on);
  if (state) renderHome();
}
$('#selectBtn').addEventListener('click', () => setSelecting(!selecting));

function renderSelectBar() {
  const bar = $('#selectBar');
  bar.classList.toggle('hidden', !selecting);
  if (!selecting) return;
  const accs = state.accounts.filter((a) => a.enabled);
  const n = accs.filter((a) => a.selected).length;
  $('#selCount').textContent = `${n} acc đã chọn · đổi chế độ`;
  $('#selAllBtn').textContent = n === accs.length ? 'Bỏ chọn hết' : 'Chọn tất cả';
}
$('#selAllBtn').addEventListener('click', async () => {
  const accs = state.accounts.filter((a) => a.enabled);
  const on = accs.some((a) => !a.selected);
  try { await Promise.all(accs.map((a) => api(`acc/${a.id}/select`, { on }))); } catch (e) { toast(e.message); }
  refresh();
});
for (const b of document.querySelectorAll('[data-mode]')) {
  b.addEventListener('click', async () => {
    try {
      const r = await api('mode', { code: b.dataset.mode });
      toast(!r.count ? 'Chưa chọn acc nào'
        : b.dataset.mode === 'auto' ? `Đã bắt đầu vòng tự động cho ${r.count} acc`
        : `Đã chuyển ${r.count} acc sang ${b.textContent} (tắt vòng tự động)`);
      setSelecting(false);
      refresh();
    } catch (e) { toast(e.message); }
  });
}

// ---------- Bảng chi tiết ----------

const COLUMNS = [
  ['Môn phái', (a) => a.sect], ['Cấp', (a) => a.level], ['Ngân lượng', (a) => a.gold],
  ['KNB (🔒 khóa)', (a) => (a.knb != null ? `${a.knb} · 🔒${a.knbLocked ?? 0}` : null)],
  ['Hòm đồ', (a) => (a.bagUsed != null ? `${a.bagUsed}/${a.bagMax}` : null)],
  ['Dã Tẩu hôm nay', (a) => (a.daTau ? (a.daTauMax ? `${a.daTau} · tối đa ${a.daTauMax}` : a.daTau) : null)],
  ['Lệnh bài có', (a) => a.ldtHave], ['LB đã dùng', (a) => a.ldtUsed], ['Mua LB hôm nay', (a) => a.ldtBuy],
  ['Phó bản', (a) => a.phoBan], ['Thuyền còn', (a) => a.boat], ['Boss tuần còn', (a) => a.weekBoss], ['LB Sát Thủ', (a) => (a.assassin ? a.assassin.split('/')[0] : null)],
  ['Chế độ', (a) => a.mode], ['Trạng thái', (a) => (a.enabled ? a.screenLabel : 'Đã tắt')],
  ['Vạn/giờ', (a) => (a.ratePerHour ? a.ratePerHour + '/giờ' : null)], ['Lên cấp', (a) => (a.levelsGained > 0 ? '+' + a.levelsGained : null)],
  ['Tầng tháp', (a) => a.tower], ['Tầng/giờ', (a) => a.floorsPerHour], ['Ra khỏi tháp', (a) => a.towerFalls || null],
  ['Đồ phổ đang giữ', (a) => ((a.doPho || []).length ? `${a.doPho.length}: ${a.doPho.join(', ')}` : null)],
  ['Đồ phổ mới nhặt', (a) => (a.lastDoPho ? `${a.lastDoPho} (${time(a.lastDoPhoAt)})` : null)],
];
const cellClass = (t, v) => (t.startsWith('Đồ phổ') && v ? 'gold' : null);

function renderTable() {
  const rows = [h('tr', {}, [h('th', { class: 'stick', text: 'Nhân vật' }), ...COLUMNS.map(([t]) => h('th', { text: t }))])];
  for (const a of state.accounts) {
    rows.push(h('tr', {}, [h('td', { class: 'stick' }, [h('span', { class: `dot ${a.enabled ? a.health : ''}`, style: 'display:inline-block;margin-right:6px' }), a.name]),
      ...COLUMNS.map(([t, f]) => h('td', { text: fmt(f(a)), class: cellClass(t, f(a)) }))]));
  }
  $('#table').replaceChildren(...rows);
}

// ---------- Nhật ký chung (gộp các acc, lọc theo loại và acc) ----------

const LOG_KINDS = [['all', 'Tất cả'], ['tower', 'Tháp'], ['dopho', 'Đồ phổ'], ['warn', 'Cảnh báo'], ['shop', 'Mua & quà']];
let logKind = 'all', logAcc = 'all', logItems = [], logTimer = null;

function kindOf(e) {
  const t = e.text;
  if (/Đồ phổ/.test(t)) return 'dopho';
  if (e.level === 'warn' || e.level === 'error' || /^⚠|^💀/.test(t)) return 'warn';
  if (/^🛒|^🎁|lệnh bài Dã Tẩu|Nhận quà/i.test(t)) return 'shop';
  if (/^🏆|^🗼|^👑 Tháp|tháp/i.test(t)) return 'tower';
  return 'other';
}

async function loadLog() {
  clearInterval(logTimer);
  logTimer = setInterval(() => { if (view === 'log') loadLog(); else clearInterval(logTimer); }, 15000);
  if (!state) return;
  const accs = state.accounts.filter((a) => a.enabled);
  const all = await Promise.all(accs.map((a) => api(`acc/${encodeURIComponent(a.id)}/activity`)
    .then((items) => items.map((e) => ({ ...e, acc: a.name, accId: a.id }))).catch(() => [])));
  logItems = all.flat().sort((x, y) => (x.at < y.at ? 1 : -1));
  renderLog();
}

function renderLog() {
  $('#logKinds').replaceChildren(...LOG_KINDS.map(([id, label]) => h('button', {
    class: logKind === id ? 'on' : '', 'aria-pressed': logKind === id ? 'true' : 'false', text: label,
    onclick: () => { logKind = id; renderLog(); },
  })));
  const sel = $('#logAcc');
  const opts = [['all', 'Tất cả acc'], ...state.accounts.filter((a) => a.enabled).map((a) => [a.id, a.name])];
  sel.replaceChildren(...opts.map(([v, t]) => { const o = h('option', { value: v, text: t }); o.selected = v === logAcc; return o; }));
  sel.onchange = () => { logAcc = sel.value; renderLog(); };

  const day = (s) => new Date(s).toLocaleDateString('vi-VN');
  const today = day(new Date()), yesterday = day(new Date(Date.now() - 86400000));
  const items = logItems.filter((e) => (logAcc === 'all' || e.accId === logAcc) && (logKind === 'all' || kindOf(e) === logKind));
  const out = [];
  let lastDay = null;
  for (const e of items.slice(0, 300)) {
    const d = day(e.at);
    if (d !== lastDay) {
      lastDay = d;
      out.push(h('div', { class: 'log-day', text: d === today ? 'Hôm nay' : d === yesterday ? 'Hôm qua' : d }));
    }
    out.push(h('div', { class: 'log-item' }, [
      h('span', { class: `dot ${kindOf(e)}`, 'aria-hidden': 'true' }),
      h('div', {}, [h('div', { class: 'log-text', text: e.text }), h('div', { class: 'log-meta', text: `${e.acc} · ${time(e.at)}` })]),
    ]));
  }
  $('#logList').replaceChildren(...(out.length ? out : [h('p', { class: 'note', text: 'Chưa có hoạt động nào khớp bộ lọc.' })]));
}

// ---------- Cài đặt ----------

function renderSettings() {
  const s = state.settings;
  const section = (title, children) => h('section', { class: 'set-section' }, [h('h3', { text: title }), ...children]);
  const toggleRow = (label, on, onclick) => h('div', { class: 'set-row' }, [h('span', { text: label }),
    h('button', { class: 'btn small' + (on ? ' on' : ''), text: on ? 'Đang bật' : 'Đang tắt', onclick })]);

  // Ô chọn thay cho ô nhập số (trên điện thoại ô số hiển thị xấu, bàn phím che): [[giá trị, chữ]]; giá trị đang dùng
  // không có trong danh sách thì thêm vào để không mất cài đặt cũ
  const pick = (options, value, fmtLabel) => {
    const opts = options.map((o) => (Array.isArray(o) ? o : [o, fmtLabel ? fmtLabel(o) : String(o)]));
    if (value != null && !opts.some(([v]) => String(v) === String(value))) opts.push([value, fmtLabel ? fmtLabel(value) : String(value)]);
    opts.sort((a, b) => (typeof a[0] === 'number' && typeof b[0] === 'number' ? a[0] - b[0] : 0));
    return h('select', {}, opts.map(([v, t]) => { const o = h('option', { value: v, text: t }); o.selected = String(v) === String(value); return o; }));
  };
  const n0 = (n) => Number(n).toLocaleString('vi-VN');
  const vanLabel = (n) => (+n ? `${n0(n)} vạn` : 'Không');
  // Kéo quái & luật đổi chế độ
  const dir = h('select', {}, s.kiteDirections.map((d) => { const o = h('option', { value: d.id, text: d.label }); o.selected = d.id === s.kiteDirection; return o; }));
  const dist = pick([120, 180, 250, 350, 450, 600], s.kiteDistance, (n) => `${n} px`);
  const interval = pick([1, 2, 3, 4, 6, 8, 10, 15, 20, 30], s.kiteInterval, (n) => `${n} giây`);
  const kiteFlee = pick([[0, 'Không chạy'], [25, '< 25% máu'], [30, '< 30% máu'], [40, '< 40% máu'], [50, '< 50% máu'], [60, '< 60% máu']], s.kiteFleeHp ?? 40);
  const box = (checked) => { const c = h('input', { type: 'checkbox' }); c.checked = checked; return c; };
  const quest = box(s.autoQuest), dg = box(s.autoDungeon), assassin = box(s.autoAssassin);
  const boat = box(s.autoBoat !== false), weekBoss = box(s.autoWeekBoss !== false);
  const teacher = box(s.eventTeacher !== false);
  const teacherCap = pick([0, 200, 400, 800, 1500], s.eventTeacherCap ?? 400, vanLabel);
  const teacherEx = box(s.eventTeacherExchange !== false);
  const dailyKnb = box(s.dailyKnb !== false);
  const sellJunk = box(s.autoSellJunk !== false);
  const sellFree = pick([5, 10, 20, 30, 40], s.autoSellFreeSlots ?? 20, (n) => `${n} ô`);
  const buffTtl = box(s.buffTtl !== false), buffTtlSp = box(s.buffTtlSpecial !== false), buffQht = box(s.buffQht !== false);
  const guild = box(s.guildShop !== false), gLdt = box(s.guildLdt !== false), gFdl = box(s.guildPhucDuyen !== false);
  const gTtsp = box(s.guildTtsp !== false), gThbt = box(s.guildThbt !== false);
  const gDonate = pick([0, 1500, 3000, 5000], s.guildDonateCap ?? 1500, vanLabel);
  const gDaily = pick([[0, 'Tắt'], [5000, '5.000 vạn'], [10000, '10.000 vạn'], [20000, '20.000 vạn (tối đa)']], s.guildDailyAmount ?? 0, vanLabel);
  const gReserve = pick([0, 1000, 3000, 5000, 10000], s.guildDonateReserve ?? 3000, vanLabel);
  const gFurnace = box(!!s.guildFurnace), gWeek = box(s.guildWeekAlert !== false);
  const gKnbOn = box(!!s.guildKnbOn);
  const gKnbAmount = pick([50, 100, 200, 300, 500], s.guildKnbAmount ?? 100, (n) => `${n0(n)} KNB`);
  const knbOn = box(!!s.knbSendOn), knbBig = box(s.knbOpenBig !== false);
  const knbMain = h('select', {}, [h('option', { value: '', text: '— Chọn —' })].concat((s.charNames || []).map((n) => {
    const o = h('option', { value: n, text: n }); o.selected = n === s.knbMainName; return o;
  })));
  const knbKeep = pick([500, 1000, 2000, 5000], s.knbKeep ?? 1000, (n) => `${n0(n)} KNB`);
  const fallback = h('select', {}, [['tower', 'Leo Tháp'], ['farm', 'Luyện Công']].map(([v, t]) => { const o = h('option', { value: v, text: t }); o.selected = v === s.autoFallback; return o; }));
  const rest = pick([30, 60, 120, 240], s.towerRest, (n) => `${n} phút`);

  // Mật khẩu game: acc đã lưu chỉ hiện tên tài khoản + nút "Đổi"
  const forms = state.accounts.map((a) => {
    const user = h('input', { placeholder: 'Tài khoản game', autocomplete: 'off', value: a.savedUser || '' });
    const pass = h('input', { type: 'password', placeholder: 'Mật khẩu game', autocomplete: 'new-password' });
    const fields = h('div', { class: 'acc-fields' + (a.hasPassword ? ' hidden' : '') }, [
      h('div', { class: 'row' }, [user, pass]),
      h('button', { class: 'btn', text: 'Lưu', onclick: async () => {
        await act(`acc/${a.id}/credentials`, { user: user.value, password: pass.value }, `Đã lưu mật khẩu cho ${a.name}`);
        pass.value = '';
        setTimeout(renderSettings, 500);
      } }),
    ]);
    return h('div', { class: 'acc-form' }, [
      h('div', { class: 'row' }, [
        h('div', {}, [h('b', { text: a.name }), h('div', { class: 'note',
          text: a.hasPassword ? `Đã lưu: ${a.savedUser} – tự đăng nhập` : 'Chưa lưu mật khẩu – không tự đăng nhập được' })]),
        a.hasPassword ? h('button', { class: 'btn small', text: 'Đổi', style: 'flex:none', onclick: () => fields.classList.toggle('hidden') }) : null,
      ]),
      fields,
    ]);
  });

  $('#settingsBody').replaceChildren(
    section('Chung', [
      toggleRow('Kêu và thông báo khi có cảnh báo mới', notifyOn, toggleNotify),
      h('div', { class: 'set-row' }, [
        h('span', {}, [h('span', { text: 'Tạm dừng server' }), h('div', { class: 'note', text: 'Thoát game cả 4 acc để chơi ở máy khác (bản PC, điện thoại). Đăng xuất trang này không dừng game.' })]),
        h('button', { class: 'btn small' + (state.paused ? ' on' : ''), text: state.paused ? 'Chạy lại' : 'Tạm dừng', onclick: () => {
          if (!state.paused && !confirm('Tạm dừng server? Cả 4 acc sẽ thoát game cho tới khi bạn bấm Chạy lại.')) return;
          act('pause', { on: !state.paused }, state.paused ? 'Đang mở lại các acc…' : 'Đã tạm dừng – các acc đã thoát game').then(() => setTimeout(renderSettings, 500));
        } }),
      ]),
      toggleRow('Tự hồi phục (tải lại, tự đăng nhập)', state.autoRecover, () => act('auto-recover', { on: !state.autoRecover }).then(() => setTimeout(renderSettings, 300))),
      h('div', { class: 'set-row' }, [h('span', { text: 'Bấm "Vào Game" ở mọi acc' }), h('button', { class: 'btn small', text: 'Vào Game', onclick: () => act('enter-all', {}, 'Đã bấm Vào Game ở mọi acc') })]),
    ]),
    section('Kéo quái khi Leo Tháp', [
      h('p', { class: 'note', text: 'Bật riêng từng acc ở nút ⋯ trên acc. Chỉ chạy khi đang ở Thông Thiên Tháp và Auto đang bật.' }),
      h('p', { class: 'note', text: 'Đây là cài đặt chung; từng acc có thể cài riêng ở nút ⋯ › Kéo quái.' }),
      h('div', { class: 'form-grid' }, [h('label', { text: 'Kiểu kéo' }), dir, h('label', { text: 'Mỗi lần chạy' }), dist, h('label', { text: 'Dừng đánh / chu kỳ' }), interval, h('label', { text: 'Máu thấp thì chạy xa' }), kiteFlee]),
    ]),
    section('Vòng tự động', [
      h('p', { class: 'note', text: 'Bấm 🔁 Auto để bắt đầu vòng. Chết nhanh 3 lần trong 15 phút thì Luyện Công, nghỉ tháp theo số phút bên dưới.' }),
      h('label', { class: 'check-row' }, [quest, '1. Dã Tẩu còn làm được → Nhiệm Vụ']),
      h('label', { class: 'check-row' }, [dg, '2. Còn lượt Phó bản → Phó Bản']),
      h('label', { class: 'check-row' }, [boat, '3. Còn chuyến thuyền → Đi thuyền']),
      h('label', { class: 'check-row' }, [weekBoss, '4. Còn lượt Boss tuần → Khiêu chiến, nhận rương tuần']),
      h('label', { class: 'check-row' }, [assassin, '5. Còn Lệnh Bài Sát Thủ → gọi Sát Thủ']),
      h('div', { class: 'form-grid' }, [h('label', { text: '6. Xong hết thì' }), fallback, h('label', { text: 'Nghỉ tháp (phút)' }), rest]),
      h('button', { class: 'btn primary', text: 'Lưu cài đặt', onclick: () => act('settings', {
        kiteDirection: dir.value, kiteDistance: +dist.value, kiteInterval: +interval.value, kiteFleeHp: +kiteFlee.value,
        autoDungeon: dg.checked, autoQuest: quest.checked, autoFallback: fallback.value, towerRest: +rest.value, autoAssassin: assassin.checked,
        autoBoat: boat.checked, autoWeekBoss: weekBoss.checked,
      }, 'Đã lưu cài đặt') }),
    ]),
    section('Sự kiện', [
      h('p', { class: 'note', text: '🌹 Tri Ân Nhà Giáo (tới hết 20/11): mỗi ngày tặng Thầy Đồ đủ 40 Bó Hoa (mức tối đa có điểm), tự ghép thêm khi sắp hết. Giấy Gói mua bằng lượng, không bao giờ tiêu KNB.' }),
      h('label', { class: 'check-row' }, [teacher, 'Tặng Bó Hoa mỗi ngày (cả 4 acc)']),
      h('label', { class: 'check-row' }, [teacherEx, 'Đổi nguyên liệu thừa: Đại Hồng Bao → Tiên Thảo Lộ Đặc Biệt → Hồng Bao (chừa đủ để tặng tới hết sự kiện)']),
      h('div', { class: 'form-grid' }, [h('label', { text: 'Chi Giấy Gói tối đa (vạn/acc/ngày)' }), teacherCap]),
      h('button', { class: 'btn primary', text: 'Lưu', onclick: () => act('settings', {
        eventTeacher: teacher.checked, eventTeacherCap: +teacherCap.value, eventTeacherExchange: teacherEx.checked,
      }, 'Đã lưu cài đặt sự kiện') }),
    ]),
    section('Việc mỗi ngày', [
      h('p', { class: 'note', text: 'Chạy lúc rảnh ở cả 4 acc, không phụ thuộc vòng tự động.' }),
      h('label', { class: 'check-row' }, [dailyKnb, '💰 Đổi vạn → KNB tới giới hạn 1.000 KNB/ngày (thiếu vạn thì mở Hồng Bao)']),
      h('p', { class: 'note', text: '✨ Giữ trạng thái có lợi: trạng thái tắt hoặc còn dưới 5 phút thì dùng 1 cái trong Hành trang, luôn chừa 10 cái mỗi loại.' }),
      h('label', { class: 'check-row' }, [buffTtl, '🌿 Tiên Thảo Lộ (+50% kinh nghiệm)']),
      h('label', { class: 'check-row' }, [buffTtlSp, '🌺 Tiên Thảo Lộ Đặc Biệt (+100% kinh nghiệm)']),
      h('label', { class: 'check-row' }, [buffQht, '🍶 Quế Hoa Tửu (+20 may mắn)']),
      h('label', { class: 'check-row' }, [sellJunk, '💰 Hành trang sắp đầy thì bấm "Bán thừa" của game (món không khớp ⚙ Nhặt đồ, không khoá)']),
      h('div', { class: 'form-grid' }, [h('label', { text: 'Bán khi còn ≤ ô trống' }), sellFree]),
      h('button', { class: 'btn primary', text: 'Lưu', onclick: () => act('settings', {
        dailyKnb: dailyKnb.checked, buffTtl: buffTtl.checked, buffTtlSpecial: buffTtlSp.checked, buffQht: buffQht.checked,
        autoSellJunk: sellJunk.checked, autoSellFreeSlots: +sellFree.value,
      }, 'Đã lưu việc mỗi ngày') }),
    ]),
    section('💰 Gửi KNB về acc chính', [
      h('p', { class: 'note', text: 'Mỗi ngày một lần (sau khi đổi vạn → KNB) các acc khác mở Đại Hồng Bao rồi gửi thư tặng phần KNB trên mức giữ lại về acc chính; acc chính tự nhận thư mỗi 30 phút. Chỉ gửi cho nhân vật trong danh sách acc.' }),
      h('label', { class: 'check-row' }, [knbOn, 'Bật gửi KNB về acc chính']),
      h('div', { class: 'form-grid' }, [h('label', { text: 'Acc chính' }), knbMain, h('label', { text: 'Mỗi acc giữ lại' }), knbKeep]),
      h('label', { class: 'check-row' }, [knbBig, '🧧 Mở hết Đại Hồng Bao trước khi gửi']),
      h('button', { class: 'btn primary', text: 'Lưu', onclick: () => {
        if (knbOn.checked && !knbMain.value) { toast('Chọn acc chính trước'); return; }
        act('settings', { knbSendOn: knbOn.checked, knbMainName: knbMain.value, knbKeep: +knbKeep.value, knbOpenBig: knbBig.checked }, 'Đã lưu gửi KNB');
      } }),
    ]),
    section('Bang hội', [
      h('p', { class: 'note', text: 'Acc chưa vào bang thì bỏ qua. Cửa hàng: mua bằng điểm cống hiến mỗi ngày (món tuần khi tuần còn lượt), đồ gửi về Hộp thư được tự nhận (chỉ thư "Cửa hàng bang").' }),
      h('label', { class: 'check-row' }, [guild, '🏯 Mua ở cửa hàng bang mỗi ngày']),
      h('label', { class: 'check-row' }, [gLdt, 'Lệnh Bài Dã Tẩu (20 điểm · 10/ngày)']),
      h('label', { class: 'check-row' }, [gFdl, 'Bình Phúc Duyên Lộ (Tiểu) (100 điểm · 10/ngày)']),
      h('label', { class: 'check-row' }, [gTtsp, 'Tiên Thảo Lộ Đặc Biệt (300 điểm · 5/tuần)']),
      h('label', { class: 'check-row' }, [gThbt, 'Tinh Hồng Bảo Thạch (60 điểm · 6/tuần)']),
      h('div', { class: 'form-grid' }, [h('label', { text: 'Thiếu điểm mua cửa hàng thì góp tối đa' }), gDonate]),
      h('p', { class: 'note', text: '💰 Cống hiến mỗi ngày: góp tới mức đã chọn (tính cả phần đã góp hôm nay); thiếu vạn thì mở Hồng Bao, hết Hồng Bao thì chừa lại số vạn bên dưới.' }),
      h('div', { class: 'form-grid' }, [h('label', { text: 'Cống hiến mỗi ngày' }), gDaily, h('label', { text: 'Hết Hồng Bao thì chừa lại' }), gReserve]),
      h('label', { class: 'check-row' }, [gKnbOn, '💎 Tự góp KNB vào quỹ bang mỗi ngày (tắt = không bao giờ góp KNB)']),
      h('div', { class: 'form-grid' }, [h('label', { text: 'KNB mỗi ngày (cũng là mức "đủ" của thành viên trong sổ KNB)' }), gKnbAmount]),
      h('p', { class: 'note', text: 'Tính cả phần đã góp tay hôm nay; luôn chừa lại số KNB ở mục "Gửi KNB về acc chính › Mỗi acc giữ lại".' }),
      h('label', { class: 'check-row' }, [gFurnace, '🔥 Lò Đốt Đồ mỗi ngày: góp Hoàng Kim bộ chung (trừ món khoá); giữ Động Sát May mắn max và Hồng Ảnh Kinh nghiệm max, cất vào Rương']),
      h('label', { class: 'check-row' }, [gWeek, '⚠ Cảnh báo điều kiện tuần (điểm tuần, 15 người vào game, quỹ duy trì) vào thứ Bảy, Chủ Nhật']),
      h('button', { class: 'btn primary', text: 'Lưu', onclick: () => act('settings', {
        guildShop: guild.checked, guildLdt: gLdt.checked, guildPhucDuyen: gFdl.checked, guildTtsp: gTtsp.checked, guildThbt: gThbt.checked,
        guildDonateCap: +gDonate.value, guildDailyAmount: +gDaily.value, guildDonateReserve: +gReserve.value,
        guildFurnace: gFurnace.checked, guildWeekAlert: gWeek.checked,
        guildKnbOn: gKnbOn.checked, guildKnbAmount: +gKnbAmount.value,
      }, 'Đã lưu cài đặt bang hội') }),
    ]),
    section('Mật khẩu acc game', [
      h('p', { class: 'note', text: 'Mật khẩu được mã hoá và chỉ lưu trên máy chủ này, dùng để tự đăng nhập lại.' }),
      ...forms,
    ]),
    section('Máy chủ', [
      h('p', { class: 'note', text: `Khung ${state.server.viewport} · tối đa ${state.server.fps || '∞'} khung/giây` }),
      h('button', { class: 'btn', text: 'Đăng xuất trang web', onclick: async () => { await api('logout', {}); showLogin(); } }),
    ]),
  );
}

// ---------- Cảnh báo: chuông + kêu + thông báo của hệ điều hành ----------

let notifyOn = prefs.get('notify', false);
let lastAlertAt = null; // cảnh báo mới nhất đã thấy; lần tải trang đầu không kêu cho cảnh báo cũ
let audio = null;

function beep() {
  try {
    audio = audio || new (window.AudioContext || window.webkitAudioContext)();
    for (const [freq, at] of [[880, 0], [660, 0.18]]) {
      const o = audio.createOscillator(), g = audio.createGain();
      o.frequency.value = freq;
      g.gain.setValueAtTime(0.2, audio.currentTime + at);
      g.gain.exponentialRampToValueAtTime(0.001, audio.currentTime + at + 0.16);
      o.connect(g).connect(audio.destination);
      o.start(audio.currentTime + at);
      o.stop(audio.currentTime + at + 0.17);
    }
  } catch (_) {}
}

function checkNewAlerts() {
  const newest = state.alerts[0]?.at || null;
  if (lastAlertAt === null) { lastAlertAt = newest || ''; return; }
  const fresh = state.alerts.filter((a) => a.at > lastAlertAt);
  if (newest && newest > lastAlertAt) lastAlertAt = newest;
  if (!fresh.length || !notifyOn) return;
  beep();
  if (window.Notification && Notification.permission === 'granted') {
    for (const a of fresh.slice(0, 3)) {
      try { new Notification(`VLMulti · ${a.account}`, { body: a.message, tag: a.account + a.message }); } catch (_) {}
    }
  }
}

async function toggleNotify() {
  notifyOn = !notifyOn;
  prefs.set('notify', notifyOn);
  renderSettings();
  if (!notifyOn) return;
  beep(); // bấm nút = cho phép trang phát tiếng
  if (!window.isSecureContext || !window.Notification) {
    toast('Đã bật tiếng kêu. Thông báo của điện thoại chỉ bật được khi mở trang qua HTTPS (Tailscale).');
  } else if (Notification.permission === 'default') {
    const p = await Notification.requestPermission();
    toast(p === 'granted' ? 'Đã bật thông báo' : 'Trình duyệt không cho hiện thông báo, chỉ kêu tiếng');
  } else if (Notification.permission === 'denied') {
    toast('Trình duyệt đang chặn thông báo của trang này, chỉ kêu tiếng');
  } else {
    toast('Đã bật kêu và thông báo');
  }
}

$('#alertsBtn').addEventListener('click', () => {
  const items = state.alerts.length
    ? state.alerts.map((a) => h('div', { class: 'alert-item' }, [h('time', { text: time(a.at) }), `${a.account}: ${a.message}`]))
    : [h('p', { class: 'note', text: 'Không có cảnh báo.' })];
  openModal('Cảnh báo gần đây', [
    ...items,
    state.alerts.length ? h('p', {}, [h('button', { class: 'btn', text: 'Xoá hết', onclick: async () => { await act('alerts/clear'); closeModal(); } })]) : null,
  ].filter(Boolean));
});

// ---------- Live: xem trực tiếp ----------

const live = { ws: null, id: null, lastTap: { x: 0.5, y: 0.5 }, frames: 0, bytes: 0, statTimer: null, pingTimer: null, drawing: false, next: null };
const liveCanvas = $('#liveCanvas');
const LIVE_RETRIES = 3;

function openLive(id, name) {
  closeLive();
  live.id = id;
  live.retries = 0;
  $('#liveTitle').textContent = name;
  $('#live').classList.remove('hidden');
  document.body.classList.add('no-scroll');
  // Điện thoại: toàn màn hình + xoay ngang (chỉ được khi trình duyệt cho phép, ví dụ app cài từ biểu tượng)
  try {
    const el = document.documentElement;
    if (matchMedia('(pointer: coarse)').matches && el.requestFullscreen && !document.fullscreenElement) {
      el.requestFullscreen().then(() => screen.orientation?.lock?.('landscape')).catch(() => {});
    }
  } catch (_) {}
  connectLive();
}

function connectLive() {
  const id = live.id;
  $('#liveInfo').textContent = '';
  $('#liveMsg').textContent = live.retries ? `Mất kết nối – đang kết nối lại (lần ${live.retries}/${LIVE_RETRIES})…` : 'Đang kết nối…';
  $('#liveMsg').classList.remove('hidden');

  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/api/acc/${encodeURIComponent(id)}/live`);
  ws.binaryType = 'blob';
  live.ws = ws;
  let opened = false, ended = false;
  ws.onopen = () => { opened = true; };
  ws.onmessage = (e) => {
    if (typeof e.data === 'string') {
      let m; try { m = JSON.parse(e.data); } catch (_) { return; }
      if (m.t === 'info') { $('#liveMsg').textContent = 'Đang chờ hình…'; live.retries = 0; }
      if (m.t === 'end') { ended = true; endLive(m.reason); }
      return;
    }
    live.frames++;
    live.bytes += e.data.size;
    live.next = e.data;   // chỉ vẽ khung mới nhất, khung cũ chưa kịp vẽ thì bỏ
    if (!live.drawing) drawNext();
  };
  // Bị ngắt mà server không báo lý do (mạng chập chờn, server khởi động lại…) → tự kết nối lại vài lần
  ws.onclose = (e) => {
    if (live.ws !== ws || ended) return;
    const why = opened ? `Mất kết nối (mã ${e.code})` : `Không kết nối được tới server (mã ${e.code}) – có thể phiên đăng nhập trang web đã hết, hãy tải lại trang`;
    endLive(why);
    if (opened && live.retries < LIVE_RETRIES && !$('#live').classList.contains('hidden')) {
      live.retries++;
      setTimeout(() => { if (!live.ws && live.id === id && !$('#live').classList.contains('hidden')) connectLive(); }, 1500);
    }
  };
  live.statTimer = setInterval(() => {
    $('#liveInfo').textContent = `${live.frames} hình/giây · ${Math.round(live.bytes / 1024)} KB/giây`;
    live.frames = 0; live.bytes = 0;
  }, 1000);
  // Giữ kết nối qua proxy (không tính là thao tác)
  live.pingTimer = setInterval(() => send({ t: 'ping' }), 25000);
}

async function drawNext() {
  live.drawing = true;
  while (live.next) {
    const blob = live.next;
    live.next = null;
    try {
      const bmp = await createImageBitmap(blob);
      if (liveCanvas.width !== bmp.width || liveCanvas.height !== bmp.height) {
        liveCanvas.width = bmp.width;
        liveCanvas.height = bmp.height;
      }
      liveCanvas.getContext('2d').drawImage(bmp, 0, 0);
      bmp.close();
      $('#liveMsg').classList.add('hidden');
    } catch (_) {}
  }
  live.drawing = false;
}

function send(m) {
  if (live.ws && live.ws.readyState === WebSocket.OPEN) live.ws.send(JSON.stringify(m));
}

function endLive(reason) {
  if (!live.ws) return;
  const ws = live.ws;
  live.ws = null;
  clearInterval(live.statTimer);
  clearInterval(live.pingTimer);
  try { ws.close(); } catch (_) {}
  $('#liveMsg').textContent = `Đã dừng xem: ${reason}`;
  $('#liveMsg').classList.remove('hidden');
  refresh();
}

function closeLive() {
  endLive('Đã đóng');
  $('#live').classList.add('hidden');
  document.body.classList.remove('no-scroll');
  try {
    screen.orientation?.unlock?.();
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  } catch (_) {}
}

// Vị trí chạm → toạ độ 0..1 trên khung hình (canvas giữ đúng tỉ lệ nên khung = cả canvas)
function liveXY(e) {
  const r = liveCanvas.getBoundingClientRect();
  return { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height };
}

liveCanvas.addEventListener('click', (e) => {
  if (!live.ws) return;
  const p = liveXY(e);
  if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) return;
  live.lastTap = p;
  send({ t: 'click', x: p.x, y: p.y });
  const mark = h('span', { class: 'tap' });
  const stage = liveCanvas.parentElement.getBoundingClientRect();
  mark.style.left = `${e.clientX - stage.left}px`;
  mark.style.top = `${e.clientY - stage.top}px`;
  liveCanvas.parentElement.append(mark);
  setTimeout(() => mark.remove(), 800);
});

// Cuộn chuột trên máy tính: gộp lại, gửi tối đa 10 lần/giây
let wheelSum = 0, wheelAt = null, wheelTimer = null;
liveCanvas.addEventListener('wheel', (e) => {
  if (!live.ws) return;
  e.preventDefault();
  wheelSum += e.deltaY;
  wheelAt = liveXY(e);
  if (wheelTimer) return;
  wheelTimer = setTimeout(() => {
    send({ t: 'wheel', x: wheelAt.x, y: wheelAt.y, dy: wheelSum });
    wheelSum = 0; wheelTimer = null;
  }, 100);
}, { passive: false });

for (const b of document.querySelectorAll('[data-wheel]')) {
  b.addEventListener('click', () => send({ t: 'wheel', x: live.lastTap.x, y: live.lastTap.y, dy: +b.dataset.wheel }));
}
for (const b of document.querySelectorAll('[data-key]')) {
  b.addEventListener('click', () => send({ t: 'key', key: b.dataset.key }));
}
$('#liveClose').addEventListener('click', closeLive);

// ---------- Ảnh nhanh ----------

function openSnapshot(id, name) {
  const img = h('img', { class: 'snapshot', alt: '' });
  const msg = h('p', { class: 'note', text: 'Đang chụp…' });
  const load = () => {
    msg.textContent = 'Đang chụp…';
    msg.classList.remove('hidden');
    img.onload = () => msg.classList.add('hidden');
    img.onerror = () => { msg.textContent = 'Không chụp được (trang đang tải lại?) – thử lại sau.'; };
    img.src = `/api/acc/${encodeURIComponent(id)}/shot?t=${Date.now()}`;
  };
  openModal(`Ảnh nhanh · ${name}`, [
    msg, img,
    h('p', { class: 'row' }, [
      h('button', { class: 'btn', text: 'Chụp lại', onclick: load }),
      h('button', { class: 'btn primary', text: 'Live', onclick: () => { closeModal(); openLive(id, name); } }),
    ]),
  ], true);
  load();
}

// ---------- Hộp thoại ----------

function openModal(title, body, wide = false) {
  $('#modalTitle').textContent = title;
  $('#modalBody').replaceChildren(...[].concat(body));
  $('#modal').classList.toggle('wide', wide);
  $('#modal').classList.remove('hidden');
}
const closeModal = () => $('#modal').classList.add('hidden');
$('#modalClose').addEventListener('click', closeModal);
$('#modal').addEventListener('click', (e) => { if (e.target.id === 'modal') closeModal(); });

// ---------- Đếm ngược Boss Hoàng Kim ----------
// Lịch của game theo giờ Việt Nam (GMT+7), không phụ thuộc múi giờ của máy đang xem
const BOSS_HK_TIMES = [[12, 0], [15, 30], [19, 30], [22, 0]];
const BOSS_HK_MINUTES = 30;
const clock = (sec) => {
  const hh = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  return (hh ? `${hh}:${String(m).padStart(2, '0')}` : `${m}`) + `:${String(s).padStart(2, '0')}`;
};
function renderBossHk() {
  const vn = new Date(Date.now() + 7 * 3600000); // giờ Việt Nam đọc bằng getUTC*
  const now = vn.getUTCHours() * 3600 + vn.getUTCMinutes() * 60 + vn.getUTCSeconds();
  const starts = BOSS_HK_TIMES.map(([hh, m]) => hh * 3600 + m * 60);
  const on = starts.find((st) => now >= st && now < st + BOSS_HK_MINUTES * 60);
  const el = $('#bossHk');
  if (on != null) {
    el.textContent = `Boss HK đang ra · còn ${clock(on + BOSS_HK_MINUTES * 60 - now)}`;
    el.classList.add('on'); // không dùng tên "live": trùng với màn Live phủ kín màn hình
  } else {
    const next = starts.find((st) => st > now) ?? starts[0] + 86400;
    el.textContent = `Boss HK sau ${clock(next - now)}`;
    el.classList.remove('on');
  }
}
renderBossHk();
setInterval(renderBossHk, 1000);

// ---------- Cài như app (PWA) ----------
// Service worker chỉ chạy được ở HTTPS (Tailscale) hoặc localhost
if ('serviceWorker' in navigator && window.isSecureContext) {
  navigator.serviceWorker.register('sw.js').catch(() => {});
}

// ---------- Bắt đầu ----------
api('state').then(() => start()).catch(() => showLogin());
