// Script nạp vào trang game của mỗi khung, chạy trước script của game.
//  - Báo trạng thái (màn hiện tại, tên, cấp, tiền, chế độ) về app.
//  - Khi app bật đồng bộ: bắt thao tác THẬT của người chơi (isTrusted) và gửi về app.
//  - Phát lại thao tác app gửi tới bằng sự kiện tổng hợp (isTrusted = false),
//    nên khung nhận không bắt lại → không bị vòng lặp.
//  - Thực hiện lệnh: đổi chế độ, vào game, đăng nhập.
(() => {
  if (window.__vlAgent) return;
  window.__vlAgent = true;
  const wv = window.chrome && window.chrome.webview;
  if (!wv || window.top !== window) return; // chỉ chạy ở trang chính, bỏ qua iframe

  const post = (m) => { try { wv.postMessage(m); } catch (_) {} };
  const $ = (s) => document.querySelector(s);
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // Bảo trì: trang game tự hỏi máy chủ khoảng 45 giây một lần (POST …/rpc/jx_bt_now), máy chủ trả "true" khi đang
  // bảo trì. Game vẫn chạy tiếp trên trang và không hiện gì → nghe lén câu trả lời đó (bọc fetch trước khi game nạp).
  let maintenance = false, maintenanceAt = 0;
  const pageFetch = window.fetch;
  if (typeof pageFetch === 'function') {
    window.fetch = function (input) {
      const p = pageFetch.apply(window, arguments);
      try {
        const url = typeof input === 'string' ? input : (input && input.url) || String(input || '');
        if (url.includes('/rpc/jx_bt_now')) {
          p.then((r) => r.clone().text())
            .then((t) => { maintenance = t.trim() === 'true'; maintenanceAt = Date.now(); })
            .catch(() => {});
        }
      } catch (_) {}
      return p;
    };
  }
  // Lâu không có câu trả lời mới (trang tải lại, game đổi cách hỏi…) → không coi là bảo trì nữa
  // Game còn hiện bảng "🛠 Máy chủ đang bảo trì" (#maint) che màn hình
  const inMaintenance = () => (maintenance && Date.now() - maintenanceAt < 5 * 60000) || (isOpen($('#maint')) && isVisible($('#maint')) && getComputedStyle($('#maint')).visibility !== 'hidden');

  const CLICKABLE = 'button, a, [role="button"], [data-msw], input, select, textarea, label, [onclick]';
  // Ô chat: không đồng bộ, tránh 4 acc gửi cùng một câu
  const NO_SYNC = '#u20in, #chatIn';
  const isField = (el) => !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT');
  const isVisible = (el) => { if (!el) return false; const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  // Cửa sổ Hành trang / Rương đang mở: đóng thì game ẩn (display:none) hoặc xoá hẳn. Không dựa vào kích thước
  // vì ở khung nhỏ cửa sổ có thể nằm ngoài vùng nhìn thấy mà vẫn đang mở.
  const isOpen = (el) => !!el && el.isConnected && getComputedStyle(el).display !== 'none';
  // Bảng/cửa sổ của game đang mở (bạn đang dùng) → các việc đọc định kỳ không đụng vào
  const anyWindowOpen = () => ['#panel', '#modal'].some((sel) => { const el = $(sel); return !!el && isVisible(el); })
    || isOpen($('#u2bagw')) || isOpen($('#u2chw'));
  // Đợt Boss Hoàng Kim (12:00 · 15:30 · 19:30 · 22:00 giờ Việt Nam, 30 phút, đệm 1 phút trước / 3 phút sau):
  // game tự đưa nhân vật đi đánh → không mở bảng, không kéo quái, để game làm
  const inBossHk = () => {
    const d = new Date(Date.now() + 7 * 3600000);
    const m = d.getUTCHours() * 60 + d.getUTCMinutes();
    return [720, 930, 1170, 1320].some((s) => m >= s - 1 && m < s + 33);
  };
  const inNoSync = (el) => !!(el && el.closest && el.closest(NO_SYNC));
  // Nút chỉ có biểu tượng thì lấy chữ từ title/data-tip
  const textOf = (el) => (el.innerText || el.value || el.title || el.getAttribute('data-tip') || '')
    .trim().replace(/\s+/g, ' ').slice(0, 60);
  // Bỏ số khi so chữ: "Ô 1 · Sinh lực ×3" ở acc này là "Ô 1 · Sinh lực ×0" ở acc kia
  const norm = (s) => (s || '').replace(/[\d.,:×%+\-/()]+/g, '').replace(/\s+/g, ' ').trim();
  const txt = (sel) => ((($(sel) || {}).textContent) || '').trim();

  const setValue = (el, value) => {
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype
      : el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };

  // Thuộc tính game dùng làm mã nút (ví dụ menu: data-u="inv", chế độ: data-msw="dg") –
  // ổn định hơn vị trí "nút thứ mấy" khi giao diện các acc khác nhau đôi chút
  const STABLE_ATTRS = ['data-u', 'data-msw', 'data-tab', 'data-k', 'data-id', 'name'];

  // Đường dẫn CSS tới phần tử, neo vào tổ tiên gần nhất có id (id của game giống nhau giữa các acc)
  const pathOf = (el) => {
    const parts = [];
    while (el && el.nodeType === 1 && el !== document.documentElement) {
      if (el.id) { parts.unshift('#' + CSS.escape(el.id)); break; }
      const p = el.parentElement;
      if (!p) break;
      const tag = el.tagName.toLowerCase();
      const attr = STABLE_ATTRS.find((a) => {
        const v = el.getAttribute(a);
        return v && [...p.children].filter((c) => c.getAttribute(a) === v).length === 1;
      });
      if (attr) {
        parts.unshift(`${tag}[${attr}="${CSS.escape(el.getAttribute(attr))}"]`);
      } else {
        const same = [...p.children].filter((c) => c.tagName === el.tagName);
        parts.unshift(tag + (same.length > 1 ? `:nth-of-type(${same.indexOf(el) + 1})` : ''));
      }
      el = p;
    }
    return parts.join(' > ');
  };

  // Tìm phần tử tương ứng ở khung nhận. Phần tử có id → tin theo id. Không có id → kiểm tra
  // chữ (bỏ số); lệch thì tìm nút khác có cùng chữ; không thấy thì thôi, không bấm bừa.
  const findOnce = (path, text) => {
    let el = null;
    try { el = $(path); } catch (_) {}
    const t = norm(text);
    const idOnly = path.startsWith('#') && !path.includes('>');
    if (el && (idOnly || !t || norm(textOf(el)) === t)) return el;
    if (t) return [...document.querySelectorAll(CLICKABLE)].find((c) => isVisible(c) && norm(textOf(c)) === t) || null;
    return null;
  };

  // Chờ phần tử xuất hiện và hiện lên (bảng vừa mở ở khung nhận có thể chưa vẽ xong)
  const waitVisible = async (path, text, ms = 1500) => {
    const end = Date.now() + ms;
    for (;;) {
      const el = findOnce(path, text);
      if (el && isVisible(el)) return el;
      if (Date.now() > end) return null;
      await sleep(100);
    }
  };

  // ---------- Bắt thao tác thật (chỉ khi app bật đồng bộ cho khung này) ----------
  let capturing = false;

  document.addEventListener('click', (e) => {
    if (!capturing || !e.isTrusted || inNoSync(e.target)) return;
    const el = (e.target.closest && e.target.closest(CLICKABLE)) || e.target;
    const r = el.getBoundingClientRect();
    post({ t: 'input', ev: {
      k: 'click', path: pathOf(el), text: el.matches(CLICKABLE) ? textOf(el) : '',
      rx: r.width ? (e.clientX - r.left) / r.width : 0.5,
      ry: r.height ? (e.clientY - r.top) / r.height : 0.5,
    } });
  }, true);

  const MODIFIERS = ['Control', 'Shift', 'Alt', 'Meta'];
  const onKey = (e) => {
    if (!capturing || !e.isTrusted || e.repeat || MODIFIERS.includes(e.key) || inNoSync(e.target)) return;
    if (isField(e.target) && e.key.length === 1) return; // chữ gõ vào ô → đồng bộ qua 'value'
    if (e.target && e.target.type === 'password') return;
    post({ t: 'input', ev: {
      k: 'key', type: e.type, key: e.key, code: e.code, keyCode: e.keyCode,
      ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey,
      field: isField(e.target) ? pathOf(e.target) : null,
    } });
  };
  document.addEventListener('keydown', onKey, true);
  document.addEventListener('keyup', onKey, true);

  const onValue = (e) => {
    if (!capturing || !e.isTrusted || !isField(e.target) || inNoSync(e.target)) return;
    if (e.target.type === 'password') return;
    if (e.type === 'change' && e.target.tagName !== 'SELECT') return; // ô chữ đã gửi qua 'input'
    post({ t: 'input', ev: { k: 'value', path: pathOf(e.target), value: e.target.value ?? '' } });
  };
  document.addEventListener('input', onValue, true);
  document.addEventListener('change', onValue, true);

  // Chuỗi sự kiện của một cú click chuột thật, tại toạ độ (x, y) trên màn hình
  const fireClick = (target, x, y, afterDown) => {
    const base = { bubbles: true, cancelable: true, composed: true, view: window, clientX: x, clientY: y, button: 0 };
    const ptr = { ...base, pointerId: 1, pointerType: 'mouse', isPrimary: true };
    target.dispatchEvent(new PointerEvent('pointerdown', { ...ptr, buttons: 1 }));
    target.dispatchEvent(new MouseEvent('mousedown', { ...base, buttons: 1 }));
    if (afterDown) afterDown();
    target.dispatchEvent(new PointerEvent('pointerup', ptr));
    target.dispatchEvent(new MouseEvent('mouseup', base));
    target.dispatchEvent(new MouseEvent('click', base));
  };

  // ---------- Phát lại (tuần tự, giữ đúng thứ tự thao tác) ----------
  const replay = async (ev) => {
    if (ev.k === 'click') {
      const el = await waitVisible(ev.path, ev.text);
      if (!el) return 'miss';
      const r = el.getBoundingClientRect();
      const x = r.left + r.width * ev.rx, y = r.top + r.height * ev.ry;
      const hit = document.elementFromPoint(x, y);
      fireClick(hit && el.contains(hit) ? hit : el, x, y, () => { if (isField(el)) el.focus(); });
      return 'ok';
    }
    if (ev.k === 'key') {
      const target = (ev.field && findOnce(ev.field, '')) || document.activeElement || document.body;
      const ke = new KeyboardEvent(ev.type, {
        key: ev.key, code: ev.code, ctrlKey: ev.ctrl, shiftKey: ev.shift, altKey: ev.alt,
        bubbles: true, cancelable: true, composed: true, view: window,
      });
      // Nhiều game còn đọc keyCode/which (không đặt được qua constructor)
      Object.defineProperty(ke, 'keyCode', { get: () => ev.keyCode });
      Object.defineProperty(ke, 'which', { get: () => ev.keyCode });
      target.dispatchEvent(ke);
      return 'ok';
    }
    if (ev.k === 'value') {
      const el = await waitVisible(ev.path, '');
      if (!el || !isField(el)) return 'miss';
      setValue(el, ev.value);
      return 'ok';
    }
    return 'unknown';
  };

  let queue = Promise.resolve();
  const enqueue = (ev) => {
    queue = queue
      .then(() => replay(ev))
      .then((r) => { if (r !== 'ok') post({ t: 'replayMiss', what: ev.text || ev.key || ev.path || ev.k, path: ev.path || '', reason: r }); })
      .catch((err) => post({ t: 'log', msg: 'replay error: ' + err.message }));
  };

  // ---------- Màn hình hiện tại ----------
  const passwordField = () => [...document.querySelectorAll('input[type="password"]')].find(isVisible) || null;
  // Màn chào mới (bản 1035): chỉ có "Bắt Đầu Trò Chơi" / "Đăng Ký Tài Khoản" / "Quên Mật Mã"; bấm nút đầu mới hiện form
  const gateStart = () => { const b = $('#ngStart'); return b && isVisible(b) ? b : null; };

  const screenOf = () => {
    if (passwordField() || gateStart()) return 'login';
    const go = $('#csGo');
    if (go && isVisible(go)) return 'charselect';
    if ($('#mswBtn')) return 'game';
    return 'loading';
  };

  // Màn chọn nhân vật có bảng "Nhân vật | <tên>" – lấy ô ngay sau nhãn đó
  const gameCharName = () => { try { const S = window.H5.G('S'); return String((S && S.name) || '').trim().slice(0, 40); } catch (_) { return ''; } };
  const charSelectName = () => {
    const label = [...document.querySelectorAll('body *')].find((el) =>
      el.children.length === 0 && el.textContent.trim() === 'Nhân vật' && isVisible(el) && !el.closest('button'));
    const value = label && label.nextElementSibling;
    return value ? value.textContent.trim().slice(0, 40) : '';
  };

  // ---------- Đăng nhập ----------
  const loginForm = () => {
    const pw = passwordField();
    if (!pw) return null;
    const inputs = [...document.querySelectorAll('input[type="text"], input[type="email"], input:not([type])')].filter(isVisible);
    // Ô tài khoản: ô chữ đang hiện, nằm ngay trước ô mật khẩu
    const user = inputs.filter((i) => i.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING).pop() || null;
    // Nút gửi: form mới "Xác Định" (#ngIn), form cũ "Đăng nhập"
    const ngIn = $('#ngIn');
    const submit = (ngIn && isVisible(ngIn) ? ngIn : null) || [...document.querySelectorAll('button, input[type="submit"], [role="button"]')]
      .find((b) => isVisible(b) && /^đăng nhập$/i.test(textOf(b))) || null;
    return { pw, user, submit };
  };

  // Thông báo đang hiện trên hộp đăng nhập (lỗi mạng, sai mật khẩu…) – mọi dòng chữ trừ nhãn cố định
  // (cả nhãn của form mới: "VÕ LÂM | Idle | Máy chủ: | Võ Lâm Idle | Xác Định | Hủy | … phiên bản 1035")
  const LOGIN_LABELS = /^(Xác nhận tên tài khoản|Tài khoản:|Mật mã:|Hiện mật mã|Nhớ tài khoản|Đăng nhập|Đăng ký|Quên mật mã\?|✕|VÕ LÂM|Idle|Máy chủ:|Võ Lâm Idle|Xác Định|Hủy|Đăng Ký Tài Khoản|Quên Mật Mã|Bắt Đầu Trò Chơi|Chưa có tài khoản\?.*|Võ Lâm Idle · phiên bản.*)$/i;
  // Lỗi đăng nhập thường hiện ở khung thông báo nổi #toast của game, ví dụ "Failed to fetch"
  const loginMessage = () => {
    const pw = passwordField();
    const box = pw && (pw.closest('.mbox') || pw.closest('form') || pw.parentElement);
    const toast = $('#toast');
    const scopes = [box, toast && isVisible(toast) ? toast : null].filter(Boolean);
    return scopes
      .flatMap((s) => [...s.querySelectorAll('*'), s])
      .filter((el) => el.children.length === 0 && isVisible(el))
      .map((el) => el.textContent.trim())
      .filter((t) => t && !LOGIN_LABELS.test(t) && !/^Mỗi tài khoản/i.test(t))
      .join(' | ')
      .slice(0, 160);
  };

  // Khi đăng nhập bằng tay (kể cả bấm qua đồng bộ từ khung khác): báo app tài khoản + mật khẩu
  // để app hỏi có lưu cho lần sau không. Bắt từ lúc nhấn chuột xuống vì game có thể ẩn form
  // trước sự kiện click. Lần đăng nhập do app tự làm thì không báo.
  let autoLoggingIn = false;
  const reportManualLogin = () => {
    if (autoLoggingIn) return;
    const f = loginForm();
    if (!f || !f.pw.value) return;
    post({ t: 'manualLogin', user: f.user ? f.user.value.trim() : '', pass: f.pw.value });
  };
  const isLoginButton = (target) => {
    const b = target && target.closest && target.closest('button, input[type="submit"], [role="button"]');
    return !!b && (b.id === 'ngIn' || /^đăng nhập$/i.test(textOf(b)));
  };
  document.addEventListener('pointerdown', (e) => { if (isLoginButton(e.target)) reportManualLogin(); }, true);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && e.target && e.target.type === 'password') reportManualLogin();
  }, true);

  // ---------- Minimap (dùng cho kéo quái) ----------
  // Đọc minimap vẽ trong canvas #arena (ô vuông ngay trên thanh tên vùng #u20zone).
  // Trả về (theo px của minimap): tâm phòng (cx, cy), vị trí nhân vật (px, py), tâm khung camera (rx, ry),
  // nửa kích thước phòng (hw, hh), và s = số px màn hình ứng với 1 px minimap.
  let minimapScale = 0;
  const TOWER_ROOM_SCREEN_WIDTH = 2550; // bề rộng phòng Thông Thiên Tháp, tính bằng px màn hình
  const readMinimap = () => {
    const c = $('#arena'), z = $('#u20zone');
    if (!c || !z || !isVisible(z)) return null;
    const cr = c.getBoundingClientRect(), zr = z.getBoundingClientRect();
    const sx = c.width / cr.width, sy = c.height / cr.height;
    const bx = Math.round((zr.left - cr.left) * sx), by = Math.round((zr.top - zr.width - cr.top) * sy);
    const W = Math.round(zr.width * sx), H = Math.round(zr.width * sy);
    if (bx < 0 || by < 0 || W < 20) return null;
    let img;
    try { img = c.getContext('2d').getImageData(bx, by, W, H); } catch (_) { return null; }
    const d = img.data, col = new Uint16Array(W), row = new Uint16Array(H);
    let fx0 = W, fx1 = -1, fy0 = H, fy1 = -1, wx = 0, wy = 0, wn = 0;
    const M = 4; // bỏ viền vàng của ô minimap
    for (let y = M; y < H - M; y++) for (let x = M; x < W - M; x++) {
      const i = (y * W + x) * 4, r = d[i], g = d[i + 1], b = d[i + 2], l = (r + g + b) / 3;
      if (r > 200 && g > 200 && b > 200) { wx += x; wy += y; wn++; }                       // chấm nhân vật
      else if (l > 45 && l < 220 && Math.abs(r - g) <= 5 && Math.abs(g - b) <= 6) { col[x]++; row[y]++; } // khung camera (xám)
      else if (l > 40 && r - b > 12) { if (x < fx0) fx0 = x; if (x > fx1) fx1 = x; if (y < fy0) fy0 = y; if (y > fy1) fy1 = y; } // sàn (nâu)
    }
    if (fx1 < 0) return null;
    // Cạnh khung camera: hai cột (hàng) có nhiều điểm xám nhất, cách nhau > 6 px
    const edges = (arr) => {
      const order = [...arr.keys()].sort((a, b) => arr[b] - arr[a]);
      const a = order[0]; if (arr[a] < 8) return null;
      const b = order.find((i) => Math.abs(i - a) > 6 && arr[i] >= arr[a] * 0.25);
      return b == null ? null : [Math.min(a, b), Math.max(a, b)];
    };
    // Tỉ lệ (px màn hình / px minimap) tính theo bề rộng phòng tháp, cố định ~2550 px màn hình
    // (đo ở khung lớn: sàn 107 px minimap × 23,8). Ở khung nhỏ minimap bị thu nhỏ, khung camera
    // quá mờ để đo, nhưng vùng sàn thì luôn đo được.
    const floorW = fx1 - fx0;
    if (floorW < 20) return null;
    minimapScale = TOWER_ROOM_SCREEN_WIDTH / floorW;

    // Tâm khung camera (= tâm màn hình) chỉ dùng khi đo được rõ (đường viền đủ dài, thường ở khung lớn).
    // Cạnh trên/dưới: đường rõ nhất, rồi tìm đường còn lại đúng chỗ dự kiến (cao = rộng × tỉ lệ màn hình).
    const ex = edges(col);
    let rx = null, ry = null;
    if (ex && col[ex[0]] >= 15 && col[ex[1]] >= 15) {
      const rw = ex[1] - ex[0], rh = rw * cr.height / cr.width;
      rx = (ex[0] + ex[1]) / 2;
      const a = [...row.keys()].reduce((m, i) => (row[i] > row[m] ? i : m), 0);
      const near = (y) => { let n = 0; for (let k = Math.round(y) - 2; k <= Math.round(y) + 2; k++) if (k >= 0 && k < H) n += row[k]; return n; };
      if (row[a] >= 8) ry = near(a + rh) >= near(a - rh) ? a + rh / 2 : a - rh / 2;
    }
    const px = wn >= 2 ? wx / wn : rx, py = wn >= 2 ? wy / wn : ry;
    if (px == null || py == null) return null;
    // Không đo được khung camera → coi nhân vật đứng giữa màn hình (camera đặt nhân vật ở giữa khi đang đánh)
    return { cx: (fx0 + fx1) / 2, cy: (fy0 + fy1) / 2, hw: floorW / 2, hh: (fy1 - fy0) / 2,
             px, py, rx: rx ?? px, ry: ry ?? py, s: minimapScale, view: [cr.left, cr.top, cr.width, cr.height] };
  };

  // Góc thứ `step` của hình vuông có một góc ở tâm phòng: về tâm → phải → chéo xuống → xuống → (về tâm)…
  const SQUARE = [[0, 0], [1, 0], [1, 1], [0, 1]];
  const squareTarget = (m, side, step) => {
    let [ox, oy] = SQUARE[step % 4].map((k) => (k * side) / m.s);
    // Kéo điểm vào trong phòng (hình thoi) nếu quá xa: |dx|/hw + |dy|/hh ≤ 0,75
    const q = Math.abs(ox) / m.hw + Math.abs(oy) / m.hh;
    if (q > 0.75) { ox *= 0.75 / q; oy *= 0.75 / q; }
    return { tx: m.cx + ox, ty: m.cy + oy };
  };

  // ---------- Lệnh từ app ----------
  const commands = {
    mode: async (code) => {
      const opt = $(`#mswMenu button[data-msw="${CSS.escape(code)}"]`);
      if (!opt) return 'not-in-game';
      if (opt.classList.contains('on')) {
        // Nút "Leo Tháp" vẫn sáng nhưng nhân vật đã bị đưa ra Luyện Công (chết trong tháp, bản game 08/11 không làm
        // mới nút) → bấm Luyện Công rồi Leo Tháp lại để game thật sự vào tháp
        const gm = gameModeNow();
        if (code === 'tower' && gm && !gm.tower && gm.m !== 'tower') {
          const farm = $('#mswMenu button[data-msw="farm"]');
          if (farm && farm !== opt) { farm.click(); await sleep(600); }
          opt.click();
          return 'ok-reset';
        }
        return 'already';
      }
      opt.click();
      return 'ok';
    },
    enter: () => {
      const go = $('#csGo');
      if (!go || !isVisible(go)) return $('#mswBtn') ? 'already' : 'not-ready';
      go.click();
      return 'ok';
    },
    login: async (arg) => {
      // Màn chào mới: bấm "Bắt Đầu Trò Chơi" rồi chờ form hiện
      const start = gateStart();
      if (!passwordField() && start) {
        start.click();
        const end = Date.now() + 4000;
        while (!passwordField() && Date.now() < end) await sleep(100);
      }
      const f = loginForm();
      if (!f) return 'no-form';
      if (!f.submit) return 'no-button';
      if (f.user && arg.user && f.user.value.trim().toLowerCase() !== arg.user.toLowerCase()) setValue(f.user, arg.user);
      setValue(f.pw, arg.pass);
      autoLoggingIn = true;
      try { f.submit.click(); } finally { autoLoggingIn = false; }
      return 'ok';
    },
    // Kéo quái (chỉ khi Leo Tháp). Auto vẫn bật, nhân vật tự đánh tiếp sau khi chạy.
    //  - square: đọc minimap để biết vị trí thật, bấm thẳng tới góc thứ `step` của hình vuông
    //    quanh tâm phòng (bị Auto/nhặt đồ kéo lệch thì lần sau tự chỉnh). Tới nơi rồi → góc kế tiếp.
    //    Không đọc được minimap → chạy (dx, dy) tính từ chỗ đang đứng như kiểu cũ.
    //  - kiểu khác: chạy (dx, dy) tính từ chỗ đang đứng (camera đặt nhân vật giữa màn hình).
    // Gọi Boss Sát Thủ (dùng 1 Lệnh Bài Sát Thủ): bảng Nhiệm vụ › 🗡 Sát Thủ › nút cấp cao nhất ≤ cấp nhân vật.
    // Phải đang Luyện Công ("Ra bãi luyện công để gọi Sát Thủ"). Nhân vật tự dịch chuyển tới; đang đánh thì góc
    // nhiệm vụ có dòng "Sát Thủ · Truy sát mục tiêu", hạ xong dòng đó biến mất.
    assassin: async () => {
      if (assassinActive()) return 'already';
      if (flowBusy || anyWindowOpen()) return 'busy';
      if (inTowerOrDungeon()) return 'need-farm';
      const btn = $('#u20menu > button[data-u="quest"]'), tab = $('#t-quest');
      if (!btn || !tab) return 'not-ready';
      const lv = parseInt(txt('#lvS'), 10) || 0;
      btn.click();
      let went = '';
      try {
        const st = tab.querySelector('[data-qt="st"]');
        if (!st) return 'no-tab';
        st.click();
        const tiers = [...tab.querySelectorAll('button[data-st]')].filter((b) => !b.disabled && +b.dataset.st <= lv);
        const best = tiers.sort((a, b) => b.dataset.st - a.dataset.st)[0];
        if (!best) return 'no-tier';
        const before = readAssassin(tab);
        if (before && before.startsWith('0/')) return 'no-token';
        best.click();
        // Game hỏi "Đi đến Boss Sát Thủ" / "Hủy bỏ nhiệm vụ" → bấm Đi đến (không bao giờ bấm Hủy)
        const go = await waitFor(assassinGoButton, 2500);
        if (go) { went = go.textContent.trim(); go.click(); }
      } finally {
        closeQuestWindows();
      }
      await sleep(600);
      const toast = $('#toast');
      const toastText = toast && isVisible(toast) ? toast.textContent.trim() : '';
      if (/luyện công/i.test(toastText)) return 'need-farm';
      if (!(await waitFor(assassinActive, 5000))) return 'no-start: ' + toastText.slice(0, 80);
      return went ? 'ok' : 'ok-nogo: ' + assassinButtonsText();
    },
    // Đang có nhiệm vụ Sát Thủ mà nhân vật không tới (vd. sau khi đăng nhập lại): bảng Nhiệm vụ › 🗡 Sát Thủ › "Đi đến…"
    assassinGo: async () => {
      if (!assassinActive()) return 'none';
      if (flowBusy || anyWindowOpen()) return 'busy';
      if (inTowerOrDungeon()) return 'need-farm';
      const btn = $('#u20menu > button[data-u="quest"]'), tab = $('#t-quest');
      if (!btn || !tab) return 'not-ready';
      btn.click();
      try {
        const st = tab.querySelector('[data-qt="st"]');
        if (st) st.click();
        const go = await waitFor(assassinGoButton, 1500);
        if (!go) return 'no-button: ' + assassinButtonsText();
        const label = go.textContent.trim();
        go.click();
        return 'ok: ' + label.slice(0, 40);
      } finally {
        closeQuestWindows();
      }
    },
    // Bảng Nhiệm vụ › ⛵ Đi thuyền › "Lên thuyền" (game tự đưa lên bản đồ Thuyền, 4 phút)
    boat: () => pressQuestButton('boat', '#boatGo'),
    // Bảng Nhiệm vụ › 👹 Boss tuần › "Khiêu chiến" (75 giây) / "Nhận rương tuần"
    weekBoss: () => pressQuestButton('wb', '#wbGo'),
    weekClaim: () => pressQuestButton('wb', '#wbClaim'),
    // "Tài khoản đang ở nơi khác" → "Chơi ở đây"
    playHere: () => {
      const b = playHereButton();
      if (!b) return 'none';
      b.click();
      return 'ok';
    },
    kite: (arg) => {
      const on = $('#mswMenu button.on');
      if (!on || on.dataset.msw !== 'tower') return 'not-tower';
      const zone = $('#u20zn');
      if (zone && zone.textContent && !/Thông Thiên Tháp/i.test(zone.textContent)) return 'not-tower';
      const auto = $('#ctrlBtn'), arena = $('#arena');
      if (!auto || !arena) return 'not-ready';
      if (!auto.classList.contains('on')) return 'manual'; // bạn đang tự tắt Auto (chơi tay) → không đụng vào
      if (flowBusy || anyWindowOpen() || inBossHk()) return 'busy'; // đang mở bảng / mua lệnh bài / đợt Boss HK → để yên

      const r = arena.getBoundingClientRect();
      const cx = r.left + r.width / 2, cy = r.top + r.height / 2;

      if (arg.mode === 'square') {
        const m = readMinimap();
        if (m) {
          let step = arg.step, result = 'ok';
          let t = squareTarget(m, arg.side, step);
          // Đã tới góc này (cách < 60 px) → nhắm luôn góc kế tiếp
          if (Math.hypot(t.tx - m.px, t.ty - m.py) * m.s < 60) { step++; t = squareTarget(m, arg.side, step); result = 'ok-next'; }
          // Vị trí góc trên màn hình = tâm màn hình + (góc − tâm khung camera) × tỉ lệ;
          // ngoài màn hình thì bấm theo hướng đó, sát mép
          let dx = (t.tx - m.rx) * m.s, dy = (t.ty - m.ry) * m.s;
          const k = Math.min(1, (r.width / 2 - 40) / Math.max(1, Math.abs(dx)), (r.height / 2 - 40) / Math.max(1, Math.abs(dy)));
          fireClick(arena, cx + dx * k, cy + dy * k);
          return result;
        }
        fireClick(arena, cx + arg.dx, cy + arg.dy);
        return 'ok-rel';
      }

      fireClick(arena, cx + arg.dx, cy + arg.dy);
      return 'ok';
    },
  };

  // ---------- 🏃 Né liên tục khi leo tháp (chạy trong trang, nhịp 0,1 giây) ----------
  // Cơ chế game (app.js jx1mAI): quái tháp ra chiêu kiểu JX1 – CHỐT vị trí nhân vật lúc bắt đầu ra chiêu rồi chiêu bay /
  // chém vào đúng điểm đó; lúc chiêu tới mà nhân vật đã rời điểm đó thì không trúng. Vì vậy có quái ở gần thì nhân vật
  // KHÔNG BAO GIỜ đứng yên: cứ ~0,3 giây nhích một đoạn ngắn (40–70 px, không đi xa nên không lên ngựa); Auto của game
  // vẫn ra đòn khi đi → vẫn hút sinh lực. Thấy quái gần vừa ra chiêu (act "at") thì nhích ngay (phản xạ).
  //  - ⚔ Cận chiến (kỹ năng chính R.P.main.melee): đi vòng quanh con quái gần nhất ở bán kính ~70% tầm đánh.
  //  - 🏹 Đánh xa: giữ khoảng cách ~55% tầm (200–320 px) tới con gần nhất, đi NGANG so với hướng quái (né chiêu bay tốt
  //    nhất), đổi chiều sau vài bước hoặc khi sát mép → zig-zag, quái đuổi theo thành hàng.
  //  - Sát mép / góc phòng: hướng vào giữa. Máu thấp (< fleeHp%): bước dài hơn, lùi khỏi đám quái. Choáng: chờ.
  // ⚠ Ngựa: game tự cho lên ngựa khi nhân vật ĐANG ĐI mà không có quái nào trong vòng 400 theo thước của game
  // (jxd = hypot(dx, 2·dy) / 0,81 → ~324 px ngang nhưng chỉ ~162 px dọc). Trên ngựa chiêu chính không dùng được, không
  // hút sinh lực. Vì vậy: mọi khoảng cách đo bằng thước của game; chỉ đi khi SAU bước đó vẫn còn quái trong vòng 330;
  // không đi lúc chưa có quái; lỡ đang ngồi ngựa mà có quái gần thì bấm xuống ngựa.
  // Ghi số liệu từng tầng (máu mất, số lần nhích, thời gian) để so và chỉnh: post "kiteFloor".
  let kiteCfg = null;
  const kiteSt = { floor: null, side: 1, lastClick: 0, target: null, from: null, steps: 0, sideSteps: 0, stuckLog: 0,
    stat: null };
  const g = (n) => { try { return window.H5.G(n); } catch (_) { return undefined; } };
  const clickWorld = (tx, ty) => {
    const arena = $('#arena'), CAM = g('CAM'), AR = g('AR');
    if (!arena || !CAM || !AR || !AR.w) return false;
    const r = arena.getBoundingClientRect();
    const k = r.width / AR.w;
    let sx = (tx - CAM.x) * k, sy = (ty - CAM.y) * k;
    // Ngoài màn hình: bấm theo đúng hướng, sát mép
    const hx = (g('H').x - CAM.x) * k, hy = (g('H').y - CAM.y) * k;
    const dx = sx - hx, dy = sy - hy;
    const lim = Math.min(1, (r.width / 2 - 30) / Math.max(1, Math.abs(dx)), (r.height / 2 - 30) / Math.max(1, Math.abs(dy)));
    if (lim < 1) { sx = hx + dx * lim; sy = hy + dy * lim; }
    fireClick(arena, r.left + sx, r.top + sy);
    return true;
  };
  const kiteFloorDone = (R) => {
    const st = kiteSt.stat;
    if (st && st.floor != null) post({ t: 'kiteFloor', floor: st.floor, secs: Math.round((Date.now() - st.t0) / 100) / 10, lost: Math.round(st.lost), steps: st.steps, minPct: Math.round(st.minPct), melee: st.melee ? 1 : 0, mounted: st.mounted || 0 });
    kiteSt.stat = null;
  };
  const kiteTick = () => {
    const c = kiteCfg;
    if (!c || !c.on) return;
    const auto = $('#ctrlBtn');
    if (!auto || !auto.classList.contains('on') || flowBusy || anyWindowOpen() || inBossHk()) return;
    const H = g('H'), R = g('R'), W = g('WORLD'), S = g('S');
    if (!H || !R || !W || !R.tower || R.dg || R.sat || R.deadT > 0 || (S && S.mode !== 'tower')) {
      if (kiteSt.stat && R && (R.deadT > 0 || !R.tower)) { kiteSt.stat.died = 1; kiteFloorDone(R); }
      kiteSt.floor = null; return;
    }
    const now = Date.now();
    const melee = c.melee != null ? !!c.melee : !!(R.P && R.P.main && R.P.main.melee);
    const rad = (R.P && R.P.main && +R.P.main.rad) || (melee ? 90 : 400);
    const m = c.margin ?? 220;
    const lifePct = R.P && R.P.life > 0 ? 100 * (R.life || 0) / R.P.life : 100;
    const low = c.fleeHp && lifePct < c.fleeHp;
    // Cận chiến: bước dài hơn (75) để ra khỏi vùng chém, nhịp nhanh hơn (0,2 giây)
    const step = Math.max(30, Math.min(melee ? Math.max(c.step ?? 75, 75) : (c.step ?? 60), 90)) * (low ? 1.3 : 1);
    const every = c.everyMs ?? (melee ? 200 : 300);
    // Thước khoảng cách của game (dọc tính gấp đôi); ngưỡng tự lên ngựa 400, giữ dưới SAFE
    const JK = 0.81, SAFE = 330;
    const jx = (ax, ay, bx, by) => Math.hypot(bx - ax, (by - ay) * 2) / JK;
    // Số liệu tầng
    if (R.tower.floor !== kiteSt.floor) {
      kiteFloorDone(R);
      kiteSt.floor = R.tower.floor;
      kiteSt.stat = { floor: R.tower.floor, t0: now, lost: 0, steps: 0, minPct: 100, life: R.life, melee };
      kiteSt.target = null;
    }
    const st = kiteSt.stat;
    if (st) { if (R.life < st.life) st.lost += st.life - R.life; st.life = R.life; st.minPct = Math.min(st.minPct, lifePct); if (R.mounted) st.mounted = (st.mounted || 0) + 1; }
    if (R.stunT > 0) return; // choáng: không đi được
    const en = (R.enemies || []).filter((e) => e && !e.dead && e.hp > 0);
    const dist = (e) => jx(H.x, H.y, e.x, e.y);
    const near = en.filter((e) => dist(e) <= SAFE);
    // Không có quái trong vòng an toàn → KHÔNG đi (đi lúc này là lên ngựa); Auto tự tới quái
    if (!near.length) return;
    // Lỡ đang ngồi ngựa mà quái ở gần → xuống ngựa (nút của game), mỗi giây tối đa một lần
    if (R.mounted && now - (kiteSt.dismountAt || 0) > 1000) {
      const rb = $('#rideBtn');
      if (rb && /Xuống ngựa/.test(rb.textContent)) { kiteSt.dismountAt = now; rb.click(); kiteSt.dismounts = (kiteSt.dismounts || 0) + 1; return; }
    }
    // Phản xạ: con quái gần vừa ra chiêu → nhích ngay, không chờ nhịp
    const swing = near.some((e) => e.act === 'at' && (e.actT ?? 1) < 0.15);
    const arrived = !kiteSt.target || Math.hypot(H.x - kiteSt.target.x, H.y - kiteSt.target.y) < 12;
    const due = now - kiteSt.lastClick >= every || (arrived && now - kiteSt.lastClick >= 150) || (swing && now - kiteSt.lastClick >= 120);
    if (!due) return;
    // Bấm rồi mà không nhúc nhích (lúc chuyển tầng, bị chặn) → ghi log, bấm lại bình thường ở lượt sau
    if (kiteSt.from && now - kiteSt.lastClick > 700 && Math.hypot(H.x - kiteSt.from.x, H.y - kiteSt.from.y) < 4 && now - kiteSt.stuckLog > 30000) {
      kiteSt.stuckLog = now;
      post({ t: 'log', msg: `kite: không nhúc nhích sau khi bấm (tầng ${R.tower.floor}, quái ${en.length}, cận chiến ${melee ? 1 : 0})` });
    }
    let t = near.reduce((a, e) => (dist(e) < dist(a) ? e : a));
    let tx, ty;
    if (melee) {
      // Đông quái sát người (≥ 3 con trong 120) → đi vòng quanh TÂM đám quái, ở mép ngoài; ít quái → quanh con gần nhất.
      // Bán kính ~70% tầm đánh (trong tầm), mỗi lần một cung dài `step` (px trên màn hình).
      // Tính trong thước của game (x/0,81, 2y/0,81): vòng tròn ở đó = elip dẹt dọc trên màn hình → luôn trong tầm đánh
      const pack = near.filter((e) => dist(e) <= 120);
      let rJ = Math.max(45, Math.min(rad * 0.7, 80));
      if (pack.length >= 3) {
        const gx = pack.reduce((a, e) => a + e.x, 0) / pack.length, gy = pack.reduce((a, e) => a + e.y, 0) / pack.length;
        const spread = Math.max(...pack.map((e) => jx(gx, gy, e.x, e.y)));
        t = { x: gx, y: gy };
        rJ = Math.max(rJ, Math.min(spread + 45, 130));
      }
      const stepJ = step / JK;
      const a = Math.atan2(2 * (H.y - t.y), H.x - t.x) + kiteSt.side * (stepJ / rJ);
      const rr = (low ? rJ + stepJ * 0.6 : rJ) * JK;
      tx = t.x + Math.cos(a) * rr; ty = t.y + Math.sin(a) * rr / 2;
      if (++kiteSt.sideSteps >= 8) { kiteSt.sideSteps = 0; kiteSt.side = -kiteSt.side; } // đổi chiều vòng cho khó đoán
    } else {
      // Đi ngang so với hướng quái + chỉnh khoảng cách (theo thước game) về ~55% tầm, tối đa 260 – luôn dưới ngưỡng ngựa
      const vx = H.x - t.x, vy = H.y - t.y, d = Math.hypot(vx, vy) || 1;
      const ux = vx / d, uy = vy / d;            // hướng từ quái → nhân vật
      const dj = dist(t);
      const want = Math.max(150, Math.min(rad * 0.55, 260));
      const fix = Math.max(-step, Math.min(step, (want - dj) * 0.4 * JK)) * (low ? 1.5 : 1);
      tx = H.x + (-uy) * step * kiteSt.side + ux * fix;
      ty = H.y + (ux) * step * kiteSt.side + uy * fix;
      if (++kiteSt.sideSteps >= 5) { kiteSt.sideSteps = 0; kiteSt.side = -kiteSt.side; }
    }
    // Sát mép / góc → kéo điểm đến vào trong, đổi chiều
    const cx = W.w / 2, cy = W.h / 2;
    if (tx < m || tx > W.w - m || ty < m || ty > W.h - m) {
      kiteSt.side = -kiteSt.side; kiteSt.sideSteps = 0;
      const k = step / (Math.hypot(cx - H.x, cy - H.y) || 1);
      tx = H.x + (cx - H.x) * k; ty = H.y + (cy - H.y) * k;
    }
    // Điểm đến phải còn quái trong vòng an toàn (không thì game cho lên ngựa) → không đi, đứng đánh
    if (!near.some((e) => jx(tx, ty, e.x, e.y) <= SAFE)) { kiteSt.side = -kiteSt.side; return; }
    go(tx, ty, swing ? 'swing' : melee ? 'orbit' : 'strafe');
    function go(x, y) {
      kiteSt.target = { x, y }; kiteSt.from = { x: H.x, y: H.y }; kiteSt.lastClick = now;
      if (kiteSt.stat) kiteSt.stat.steps++;
      clickWorld(x, y);
    }
  };
  setInterval(() => { try { kiteTick(); } catch (err) { post({ t: 'log', msg: 'kite: ' + err.message }); kiteCfg = null; } }, 100);

  // 🐎 Trong Tháp không cưỡi ngựa: game tự lên ngựa khi Auto đi tới con quái ở xa (ngoài vòng 400), tới nơi lại phải
  // xuống ngựa (0,6 giây) mới đánh được, trên ngựa không đánh / không hút sinh lực. Vào tháp → tắt "Tự lên ngựa"
  // (S.autoRide) và xuống ngựa nếu đang cưỡi; ra khỏi tháp → bật lại (chỉ khi chính tool đã tắt, nhớ trong trình duyệt
  // của acc). Tắt "Tự lên ngựa" mà còn ngồi ngựa thì game không tự xuống để đánh nữa → luôn xuống ngựa khi ở tháp.
  let rideAt = 0;
  setInterval(() => {
    try {
      let S, R;
      try { S = window.H5.G('S'); R = window.H5.G('R'); } catch (_) { return; }
      if (!S || !R || !S.name) return;
      const key = 'vlRideOff';
      if (R.tower) {
        if (S.autoRide !== false) {
          S.autoRide = false;
          try { localStorage.setItem(key, '1'); } catch (_) {}
          post({ t: 'log', msg: 'ride: vào tháp → tắt Tự lên ngựa' });
        }
        if (R.mounted && Date.now() - rideAt > 1500) {
          const rb = $('#rideBtn');
          if (rb && /Xuống ngựa/.test(rb.textContent)) { rideAt = Date.now(); rb.click(); }
        }
      } else {
        let mine = false;
        try { mine = localStorage.getItem(key) === '1'; } catch (_) {}
        if (mine && !R.dg) {
          S.autoRide = true;
          try { localStorage.removeItem(key); } catch (_) {}
          post({ t: 'log', msg: 'ride: ra khỏi tháp → bật lại Tự lên ngựa' });
        }
      }
    } catch (_) {}
  }, 1000);

  // ---------- Tự mua Lệnh Bài Dã Tẩu (khi bấm 📜 Nhiệm Vụ) ----------
  // Kỳ Trân Các: mỗi lệnh bài 20 KNB khóa, tối đa 100 lần/ngày. Thiếu KNB khóa → đổi KNB thường (1:1, game hỏi lại
  // ở #askBox → bấm "Đổi"). Hết KNB thường → mở Đại Hồng Bao (100–1000 KNB, máy chủ quay số) rồi đổi tiếp.
  // Mỗi bước đều chờ game cập nhật số liệu; không thấy đổi thì dừng, không bấm tiếp cho chắc.
  const LDT_PRICE = 20;
  let flowBusy = false;
  const waitFor = async (fn, ms = 4000) => {
    const end = Date.now() + ms;
    for (;;) { const v = fn(); if (v) return v; if (Date.now() > end) return null; await sleep(100); }
  };
  // Ô KNB trên cùng ("17.2k 🔒1113"): dùng chung VLParse với mọi chỗ khác (trước đây chỗ này không hiểu "17.2k"
  // → acc trên 10 nghìn KNB bị coi là không có KNB, không mua được lệnh bài)
  const knbNow = () => VLParse.knb(txt('#knbTop'));
  const shopOpen = () => { const m = $('#modal'); return !!m && isVisible(m) && !!$('#mBody [data-kb="0"]'); };
  const openShop = async () => {
    if (shopOpen()) return true;
    const btn = $('#u20ktc');
    if (!btn) return false;
    btn.click();
    return !!(await waitFor(shopOpen, 3000));
  };
  const closeModal = () => { const c = $('#mClose'); if (c && isVisible(c)) c.click(); };
  // Đóng hộp thoại và chắc chắn nó đã đóng (lần mua hỏng từng để Kỳ Trân Các mở → mọi lượt sau báo "đang bận")
  const closeModalSure = async () => {
    for (let i = 0; i < 3 && isVisible($('#modal')); i++) {
      closeModal();
      if (await waitFor(() => !isVisible($('#modal')), 1500)) return;
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }));
    }
    if (isVisible($('#modal'))) post({ t: 'log', msg: 'modal did not close: ' + (($('#mBody h3') || {}).textContent || '') });
  };
  const ldtCard = () => { const b = $('#mBody [data-ktc="ldt"]'); return b ? b.closest('.u2card') : null; };
  const ldtState = () => {
    const c = ldtCard();
    return c ? VLParse.ldtShop(c.querySelector('.kc-s')?.textContent) : null;
  };
  const showLdtTab = async () => {
    const t = $('#mBody [data-kb="0"]'); if (t) t.click();
    const u = $('#mBody [data-kc="1"]'); if (u) u.click();
    return !!(await waitFor(ldtState, 2000));
  };
  // Mua n lệnh bài: ×10 khi còn ≥ 10, còn lại bấm Mua từng cái; chờ số "Hôm nay" tăng sau mỗi lần bấm.
  // Không thấy số tăng (game vẽ lại bảng / đổi thẻ giữa chừng – đã gặp: mua được 10 rồi đứng): mở lại thẻ lệnh bài,
  // đọc lại số từ đầu rồi thử tiếp, tối đa 3 lần liền; thử lại vẫn không được mới thôi.
  const buyLdt = async (n) => {
    let done = 0, misses = 0;
    const base = ldtState();
    while (done < n) {
      const st = ldtState() || (await showLdtTab() && ldtState());
      if (!st || st.bought >= st.max) break;
      const step = n - done >= 10 ? 10 : 1;
      const btn = ldtCard()?.querySelector(step === 10 ? 'button[data-ktc10="ldt"]' : 'button[data-ktc="ldt"]');
      if (!btn || btn.disabled) break;
      btn.click();
      const now = await waitFor(() => { const s2 = ldtState(); return s2 && s2.bought > st.bought ? s2 : null; });
      if (now) { misses = 0; done += now.bought - st.bought; continue; }
      post({ t: 'log', msg: `buy ldt: no change after click (${misses + 1}/3): card=${(ldtCard()?.innerText || '-').replace(/\s+/g, ' ').slice(0, 120)}` });
      if (++misses >= 3) break;
      await showLdtTab();
      // Lần bấm vừa rồi có thể đã được tính mà bảng chưa vẽ lại: tính theo số "Hôm nay" thật
      const fresh = ldtState();
      if (fresh && base) done = fresh.bought - base.bought;
    }
    return done;
  };
  // Đổi `amount` KNB thường → KNB khóa (bội số của 10), bằng các nút 1000/100/10
  const convertKnb = async (amount) => {
    let left = amount, done = 0;
    const t = $('#mBody [data-kb="1"]'); if (t) t.click();
    for (const step of [1000, 100, 10]) {
      while (left >= step) {
        const btn = await waitFor(() => $(`#mBody [data-k2k="${step}"]`), 2000);
        const before = knbNow();
        if (!btn || !before) return done;
        btn.click();
        const ok = await waitFor(() => {
          const b = $('#askBox button[data-a="1"]');
          return b && isVisible(b) && /Đổi/.test($('#askBox').textContent) ? b : null;
        }, 2000);
        if (!ok) return done;
        ok.click();
        if (!(await waitFor(() => { const k = knbNow(); return k && k.khoa > before.khoa; }))) return done;
        left -= step; done += step;
      }
    }
    return done;
  };
  // Mở 1 Đại Hồng Bao: Hành trang › 🧧 → hộp "Đại Hồng Bao" › "Dùng 1". Trả về số KNB nhận được (0 nếu không mở được)
  const bigRedCount = () => hbCounts().dhb;
  // Mở Đại Hồng Bao (100–1000 KNB mỗi cái): tối đa `max` cái một lần bấm (1 = mở 1; Infinity = nút "Mở ×tất cả").
  // Trả về số KNB nhận được (0 nếu không mở được)
  const openBigRed = async (max = 1) => {
    let got = 0;
    try {
      if (!(await openHbModal())) return 0;
      const b = hbButton('dhb', max);
      if (!b) return 0;
      const before = knbNow();
      b.click();
      const after = await waitFor(() => { const k = knbNow(); return k && before && k.knb > before.knb ? k : null; }, 8000);
      if (after) await sleep(1500); // mở nhiều cái: chờ số dừng tăng
      const fin = knbNow();
      got = fin && before ? fin.knb - before.knb : 0;
    } finally {
      await closeModalSure();
      closeBag();
    }
    return got;
  };

  // ---------- Đồ trong Hành trang: tìm theo ảnh (ô đồ không ghi tên) ----------
  // img/ep/hb.png Hồng Bao · tt.png Tiên Thảo Lộ · ttsp.png Tiên Thảo Lộ Đặc Biệt · qht.png Quế Hoa Tửu
  const bagItem = (img) => [...document.querySelectorAll('#t-inv .it')].find((b) => b.querySelector(`img[src$="/${img}.png"]`));
  const bagCount = (img) => { const b = bagItem(img); return b ? +((b.querySelector('i') || {}).textContent || 0) || 0 : 0; };
  const openBag = async () => {
    const inv = $('#u20menu > button[data-u="inv"]');
    if (!inv) return false;
    if (!isOpen($('#u2bagw'))) inv.click();
    return !!(await waitFor(() => $('#bHbOpen'), 2000));
  };
  const closeBag = () => { const w = $('#u2bagw'), x = $('#u2bwx'); if (x && isOpen(w)) x.click(); };
  // Bấm món `img` trong Hành trang → hộp của món (tiêu đề khớp `title`) → nút `sel` (#kiUse "Dùng 1" / #kiUse10 "Mở ×10"),
  // chờ `changed()` báo đã có tác dụng rồi đóng hộp. Trả về true nếu thấy tác dụng.
  const useBagItem = async (img, title, sel, changed) => {
    const it = bagItem(img);
    if (!it) return false;
    it.click();
    const btn = await waitFor(() => {
      const h = $('#mBody h3'), b = $(sel);
      return h && title.test(h.textContent.trim()) && b && isVisible(b) && !b.disabled ? b : null;
    }, 2000);
    if (!btn) { await closeModalSure(); return false; }
    btn.click();
    const ok = !!(await waitFor(changed, 6000));
    if (isVisible($('#askBox'))) post({ t: 'log', msg: 'use item: unexpected dialog ' + $('#askBox').innerText.replace(/\s+/g, ' ').slice(0, 120) });
    await closeModalSure();
    return ok;
  };

  // Ngân lượng (vạn) ở ô #gold ("12.754 vạn")
  const silverNow = () => VLParse.silver(txt('#gold'));
  // 🧧 Hồng Bao / Đại Hồng Bao lưu trên máy chủ (không nằm trong Hành trang, từ bản 1041): Hành trang › nút 🧧 (#bHbOpen,
  // title "Hồng Bao 315 · Đại Hồng Bao 4") → hộp "🧧 Hồng Bao" với nút data-hbo="hb"|"dhb" data-n="1"|"10"|"N" (Mở 1 / ×10 / ×tất cả)
  const hbCounts = () => {
    const t = (($('#bHbOpen') || {}).title || '') + ' ' + ((isVisible($('#modal')) && /Hồng Bao/.test(txt('#mBody h3'))) ? ($('#mBody') || {}).innerText || '' : '');
    const big = t.match(/Đại Hồng Bao:?\s*([\d.]+)/i);
    const small = t.replace(/Đại Hồng Bao:?\s*[\d.]+/gi, '').match(/Hồng Bao:?\s*([\d.]+)/i);
    return { hb: small ? +small[1].replace(/\./g, '') : 0, dhb: big ? +big[1].replace(/\./g, '') : 0 };
  };
  const hbModalOpen = () => isVisible($('#modal')) && /Hồng Bao/.test(txt('#mBody h3')) && !!$('#mBody [data-hbo]');
  const openHbModal = async () => {
    if (hbModalOpen()) return true;
    await closeModalSure();
    if (!(await openBag())) return false;
    const b = await waitFor(() => { const x = $('#bHbOpen'); return x && isVisible(x) ? x : null; }, 2000);
    if (!b) return false;
    b.click();
    return !!(await waitFor(hbModalOpen, 3000));
  };
  // Nút mở nhiều nhất không quá `max` cái của loại `kind` (hb | dhb)
  const hbButton = (kind, max = Infinity) => {
    const bs = [...document.querySelectorAll(`#mBody [data-hbo="${kind}"]`)].filter((b) => !b.disabled && +b.dataset.n > 0 && +b.dataset.n <= max);
    return bs.sort((a, b) => +b.dataset.n - +a.dataset.n)[0] || null;
  };
  // Thiếu vạn lượng: mở Hồng Bao (1–1000 vạn mỗi cái, máy chủ quay số), ×10 mỗi lần, tới khi đủ `need` vạn hoặc hết Hồng Bao.
  const openRedForSilver = async (need, rounds = 5) => {
    const res = { opened: 0, gained: 0 };
    const start = silverNow();
    if (start == null || !(await openHbModal())) return res;
    try {
      for (let i = 0; i < rounds && (silverNow() ?? 0) < need; i++) {
        const b = hbButton('hb', 10);
        if (!b) break;
        const n = +b.dataset.n, before = silverNow() ?? 0;
        b.click();
        if (!(await waitFor(() => (silverNow() ?? 0) > before, 6000))) break;
        res.opened += n;
        await sleep(400);
      }
    } finally {
      await closeModalSure();
      closeBag();
    }
    res.gained = (silverNow() ?? start) - start;
    return res;
  };

  // ---------- Đổi vạn lượng → KNB: Kỳ Trân Các › 💰 Bảng Kim Nguyên Bảo › Đổi KNB (1 vạn = 1 KNB, tối đa 1.000 / ngày) ----------
  // Mỗi ngày một lần (app gọi). Không đủ vạn cho phần còn đổi được → mở Hồng Bao trước. Bấm ×100 / ×10 / Mua,
  // chờ số "Hôm nay đã đổi" tăng sau mỗi lần bấm; không thấy tăng 3 lần liền thì thôi.
  const knbCard = () => { const b = $('#mBody [data-ktc="knb"]'); return b ? b.closest('.u2card') : null; };
  const knbState = () => { const c = knbCard(); return c ? VLParse.knbShop(c.innerText) : null; };
  const showKnbTab = async () => {
    $('#mBody [data-kb="1"]')?.click();
    $('#mBody [data-kc="3"]')?.click();
    return !!(await waitFor(knbState, 2000));
  };
  const knbFlow = async () => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    flowBusy = true;
    const res = { r: 'ok', bought: 0, spent: 0, opened: 0, gained: 0, left: 0 };
    try {
      const open = async () => (await openShop()) && (await showKnbTab());
      if (!(await open())) {
        reportUnreadable('knb-shop', (knbCard() || $('#mBody') || {}).innerText || '');
        return { ...res, r: 'no-shop' };
      }
      let st = knbState(), misses = 0, topped = false;
      for (let i = 0; i < 40 && st && st.left > 0; i++) {
        const silver = silverNow();
        if (silver == null) { res.r = 'silver-unreadable'; reportUnreadable('silver', txt('#gold')); break; }
        if (!topped && silver < st.left * st.price) {
          topped = true; // chỉ mở Hồng Bao một đợt
          await closeModalSure();
          const got = await openRedForSilver(st.left * st.price);
          res.opened += got.opened; res.gained += got.gained;
          if (!(await open())) { res.r = 'no-shop'; break; }
          st = knbState();
          continue;
        }
        const step = [100, 10, 1].find((n) => n <= st.left && n * st.price <= silver);
        if (!step) { res.r = 'no-silver'; break; }
        const sel = step === 100 ? 'button[data-ktc100="knb"]' : step === 10 ? 'button[data-ktc10="knb"]' : 'button[data-ktc="knb"]';
        const btn = knbCard()?.querySelector(sel);
        if (!btn || btn.disabled) { res.r = 'no-button'; break; }
        btn.click();
        const now = await waitFor(() => { const x = knbState(); return x && x.done > st.done ? x : null; });
        if (isVisible($('#askBox'))) { post({ t: 'log', msg: 'knb: unexpected dialog ' + $('#askBox').innerText.replace(/\s+/g, ' ').slice(0, 120) }); res.r = 'dialog'; break; }
        if (!now) {
          if (++misses >= 3) { res.r = 'stuck'; break; }
          await showKnbTab();
          st = knbState() || st;
          continue;
        }
        misses = 0;
        res.bought += now.done - st.done; res.spent += (now.done - st.done) * st.price;
        st = now;
      }
      if (st) { res.left = st.left; res.done = st.done; }
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      try { await closeModalSure(); } catch (_) {}
      flowBusy = false;
    }
    return res;
  };

  // ---------- Giữ trạng thái có lợi: Tiên Thảo Lộ (+50% KN), Tiên Thảo Lộ Đặc Biệt (+100% KN), Quế Hoa Tửu (+20 may mắn) ----------
  // Cả ba cộng dồn thời gian. Thanh #skBuffs (mỗi trạng thái một ô, ảnh trùng ảnh món đồ, "40h" / "1m" / "22s") không có
  // hoặc còn dưới arg.minLeft giây → dùng 1 cái trong Hành trang, luôn chừa lại arg.keep cái. Đủ cả thì không mở gì.
  const BUFF_TITLES = { tt: /^Tiên Thảo Lộ\s*×/, ttsp: /^Tiên Thảo Lộ Đặc Biệt/, qht: /^Quế Hoa Tửu/ };
  // Giây còn lại: 0 = không có; null = có mà không đọc được chữ
  const buffSecs = (img) => {
    const el = [...document.querySelectorAll('#skBuffs .sti')].find((x) => x.querySelector(`img[src$="/${img}.png"]`));
    return el ? VLParse.buffLeft((el.querySelector('b') || {}).textContent) : 0;
  };
  const buffFlow = async (arg) => {
    const keep = arg.keep ?? 10, minLeft = arg.minLeft ?? 300;
    const want = (arg.items || []).filter((k) => BUFF_TITLES[k] && (buffSecs(k) ?? Infinity) < minLeft);
    if (!want.length) return { r: 'ok', used: '', low: '' };
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    flowBusy = true;
    const used = [], low = [], failed = [];
    try {
      if (!(await openBag())) return { r: 'no-bag' };
      for (const k of want) {
        const have = bagCount(k);
        if (have <= keep) { low.push(`${k}:${have}`); continue; }
        const before = buffSecs(k) ?? 0;
        if (await useBagItem(k, BUFF_TITLES[k], '#kiUse', () => bagCount(k) < have || (buffSecs(k) ?? 0) > before)) used.push(`${k}:${have - 1}`);
        else failed.push(k);
      }
    } catch (err) {
      failed.push('error: ' + err.message);
    } finally {
      try { await closeModalSure(); closeBag(); } catch (_) {}
      flowBusy = false;
    }
    return { r: failed.length ? 'failed' : 'ok', used: used.join(','), low: low.join(','), failed: failed.join(',') };
  };

  // ---------- 🏯 Cửa hàng bang: mua món giới hạn ngày / tuần bằng điểm cống hiến ----------
  // Bang hội (menu data-u="bang") › 🛒 Cửa hàng (data-bht="shop") › mục (data-bhsc) › nút data-bhmua="<món>" data-sl="1|10|…"
  // → game hỏi lại ở #askBox ("Đổi 1 … với 20 điểm cống hiến?") → "Đồng ý" (data-a="1"). Thiếu điểm → góp vạn ở
  // 💰 Cống hiến (#bhGopV + #bhGopVb, 1 vạn = 1 điểm), không quá arg.donateCap vạn mỗi ngày (tính cả phần đã góp hôm nay).
  // Món mua xong gửi vào 📨 Hộp thư ("Cửa hàng bang: 1 Lệnh Bài Dã Tẩu") → chỉ nhận đúng các thư đó, không bấm "Nhận tất cả"
  // (thư Chợ trả đồ phổ hết hạn bày bán… để bạn tự xử lý). Chưa vào bang (không có thẻ Cửa hàng) → 'no-guild'.
  const GUILD_CAT = { ldt: 'vp', fdl1: 'vp', ttsp: 'vp', thbt: 'vp', tn: 'vp', tdt: 'vp' };
  const guildBtn = (id, sl) => $(`#mBody [data-bhmua="${id}"][data-sl="${sl}"]`);
  const guildState = (id) => { const c = guildBtn(id, 1)?.closest('.u2card'); return c ? VLParse.guildItem(c.innerText) : null; };
  const guildPts = () => VLParse.guildPoints(($('#mBody') || {}).innerText);
  // Bấm nút rồi đồng ý câu hỏi của game (chỉ khi câu hỏi đúng là về điểm cống hiến / góp)
  const pressAndAgree = async (btn, re) => {
    btn.click();
    const ok = await waitFor(() => {
      const box = $('#askBox');
      if (!box || !isVisible(box) || !re.test(box.innerText)) return null;
      return [...box.querySelectorAll('button')].find((b) => b.dataset.a === '1' || /^Đồng ý$/.test(b.textContent.trim()));
    }, 2500);
    if (!ok) {
      const box = $('#askBox');
      if (box && isVisible(box)) {
        post({ t: 'log', msg: 'guild: unexpected dialog ' + box.innerText.replace(/\s+/g, ' ').slice(0, 120) });
        [...box.querySelectorAll('button')].find((b) => /^Hủy$/.test(b.textContent.trim()))?.click();
      }
      return false;
    }
    ok.click();
    return true;
  };
  const guildFlow = async (arg) => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    const menu = $('#u20menu > button[data-u="bang"]');
    if (!menu) return { r: 'no-guild' };
    flowBusy = true;
    const res = { r: 'ok', bought: [], spent: 0, donated: 0, mails: 0, points: null,
      daily: 0, redOpened: 0, burned: 0, burnPts: 0, kept: [], stashed: 0, week: null };
    try {
      await guildShop(arg, menu, res);
      if (res.r !== 'no-guild') await guildMail(res);
      if (res.r !== 'no-guild') await guildExtras(arg, res);
    } finally {
      flowBusy = false;
    }
    res.bought = res.bought.join(',');
    res.kept = res.kept.join(', ');
    return res;
  };

  // ---------- 🏯 Bang hội: cống hiến hằng ngày, Lò Đốt Đồ, duyệt đơn, điều kiện tuần ----------
  // Trạng thái bang đọc qua cầu nối của game (H5.G): BH.d.bang (cấp, quỹ, điểm tuần / mục tiêu, miễn xét, cảnh báo),
  // BH.d.toi (chức vụ, góp hôm nay / tối đa), BH.d.tv (thành viên, lần vào game cuối), BH.d.don (đơn xin vào).
  const gameGet = (name) => { try { return window.H5 && window.H5.G ? window.H5.G(name) : undefined; } catch (_) { return undefined; } };
  const openGuildTab = async (tab) => {
    if (!$('#mBody [data-bht]')) {
      await closeModalSure();
      $('#u20menu > button[data-u="bang"]')?.click();
      if (!(await waitFor(() => $('#mBody [data-bht]'), 3000))) return false;
    }
    const t = $(`#mBody [data-bht="${tab}"]`);
    if (!t) return false;
    t.click();
    await sleep(500);
    return true;
  };
  // Đồ Hoàng Kim giữ lại (không đốt) và cất vào Rương: dòng `attr` đạt mức cao nhất của món, chỉ với bộ có tên bắt đầu `set`.
  // Mức cao nhất tra trong dữ liệu game: JX.sets.gold (mẫu món theo grp/sid → mã dòng phụ) và JX.ge (mã → thuộc tính, [min,max]).
  const goldLineMax = (it, attr) => {
    const JX = window.JX;
    if (!JX || !it || !it.set) return null;
    const tpl = (JX.sets && JX.sets.gold || []).find((t) => t.grp === it.set.grp && t.sid === it.set.sid);
    if (!tpl) return null;
    for (const id of tpl.mag || []) {
      const g = JX.ge && JX.ge[id];
      if (g && JX.attr[g.a] === attr && g.p && g.p[0]) return g.p[0][1];
    }
    return null;
  };
  const goldLineValue = (it, attr) => {
    const JX = window.JX;
    const m = (it.mag || []).find((x) => JX && JX.attr[x.a] === attr);
    return m && m.p ? +m.p[0] : null;
  };
  const goldKeep = (it, rules) => rules.some((r) => {
    if (!new RegExp('^' + r.set, 'i').test(it.n || '')) return false;
    const v = goldLineValue(it, r.attr), max = goldLineMax(it, r.attr);
    return v != null && max != null && v >= max;
  });
  const stashItem = async (uid) => {
    if (!(await openBag())) return false;
    const it = $(`#t-inv [data-uid="${uid}"]`);
    if (!it) return false;
    it.click();
    const b = await waitFor(() => { const x = $('#bStashIt'); return x && isVisible(x) && !x.disabled ? x : null; }, 2000);
    if (!b) { await closeModalSure(); return false; }
    b.click();
    const ok = !!(await waitFor(() => !$(`#t-inv [data-uid="${uid}"]`), 3000));
    await closeModalSure();
    return ok;
  };
  const guildExtras = async (arg, res) => {
    try {
      // 1. Cống hiến vạn tới arg.dailyAmount trong ngày (không quá mức tối đa của game). Thiếu vạn → mở Hồng Bao;
      //    hết Hồng Bao → chừa arg.reserve vạn.
      if (arg.dailyAmount > 0 && (await openGuildTab('gop'))) {
        const card = $('#bhGopV')?.closest('.u2card');
        const info = card ? VLParse.guildDonate(card.innerText) : null;
        let want = info ? Math.max(0, Math.min(arg.dailyAmount, info.cap) - info.done) : 0;
        if (want > 0) {
          if ((silverNow() ?? 0) < want) {
            await closeModalSure();
            const got = await openRedForSilver(want, 12);
            res.redOpened = got.opened;
          }
          await openGuildTab('gop');
          await openBag();
          const hbLeft = hbCounts().hb > 0;
          closeBag();
          await openGuildTab('gop');
          const silver = silverNow() ?? 0;
          const room = hbLeft ? silver : silver - (arg.reserve ?? 3000);
          const amount = Math.max(0, Math.min(want, Math.floor(room)));
          const input = $('#bhGopV'), btn = $('#bhGopVb');
          if (amount > 0 && input && btn) {
            const before = guildPts();
            setValue(input, String(amount));
            if (await pressAndAgree(btn, /góp|cống hiến/i)) {
              const after = await waitFor(() => { const p = guildPts(); return p != null && p > (before ?? 0) ? p : null; }, 4000);
              res.daily = after != null && before != null ? after - before : amount;
            }
          }
        }
      }
      // 2. Lò Đốt Đồ: góp Hoàng Kim bộ chung trong Hành trang, trừ món khoá và món giữ lại (cất vào Rương)
      if (arg.furnace && (await openGuildTab('lo'))) {
        await waitFor(() => /Lò đợt|Chưa có Hoàng Kim/.test(($('#mBody') || {}).innerText || ''), 4000);
        const S = gameGet('S'), BH = gameGet('BH'), gop = gameGet('bhLoGop');
        const grp = BH && BH.lo && Array.isArray(BH.lo.grp) ? BH.lo.grp : [];
        const rules = arg.keep || [];
        const gold = (S && S.inv || []).filter((i) => i && i.set && i.set.kind === 'gold');
        const keepers = gold.filter((i) => goldKeep(i, rules));
        res.kept = keepers.map((i) => i.n);
        const burn = gold.filter((i) => !i.lock && !hasInnate(i) && !keepers.includes(i) && grp.includes(+i.set.grp));
        if (burn.length && typeof gop === 'function') {
          const before = gold.length;
          const p = gop(burn.slice(0, 200));
          const yes = await waitFor(() => {
            const box = $('#askBox');
            if (!box || !isVisible(box) || !/Lò Đốt Đồ/.test(box.innerText)) return null;
            return [...box.querySelectorAll('button')].find((b) => b.dataset.a === '1' || /^(Đồng ý|Góp|OK)$/i.test(b.textContent.trim()));
          }, 3000);
          if (yes) {
            yes.click();
            try { await Promise.race([p, sleep(8000)]); } catch (_) {}
            const now = (gameGet('S').inv || []).filter((i) => i && i.set && i.set.kind === 'gold').length;
            res.burned = Math.max(0, before - now);
            res.burnPts = res.burned * ((BH.lo && +BH.lo.diem) || 20);
          } else {
            const box = $('#askBox');
            if (box && isVisible(box)) { post({ t: 'log', msg: 'furnace: unexpected dialog ' + box.innerText.replace(/\s+/g, ' ').slice(0, 120) }); [...box.querySelectorAll('button')].find((b) => /^Hủy$/.test(b.textContent.trim()))?.click(); }
          }
        }
        await closeModalSure();
        for (const k of keepers) if (await stashItem(k.uid)) res.stashed++;
        closeBag();
      }
      // 3. Điều kiện tuần (đơn xin vào bang: bạn tự duyệt, tool không bấm) (game xét thứ Hai: ≥ 15 người vào game trong tuần, đủ điểm tuần, quỹ ≥ 20.000 vạn × cấp)
      const d = (gameGet('BH') || {}).d;
      if (d && d.bang) {
        const vn = new Date(Date.now() + 7 * 3600000);
        const monday = Date.UTC(vn.getUTCFullYear(), vn.getUTCMonth(), vn.getUTCDate() - ((vn.getUTCDay() + 6) % 7)) - 7 * 3600000;
        const tv = d.tv || [];
        res.week = {
          name: d.bang.ten, cap: d.bang.cap | 0, fund: +d.bang.quy_luong || 0, points: +d.bang.tuan_diem || 0, target: +d.bang.tuan_muc || 0,
          members: tv.length, active: tv.filter((m) => +m.on >= monday).length, exemptUntil: +d.bang.mien_den || 0,
          warning: d.bang.canh_bao || '', role: (d.toi && d.toi.chuc) || '',
        };
      }
    } catch (err) {
      post({ t: 'log', msg: 'guild extras: ' + err.message });
    } finally {
      try { await closeModalSure(); closeBag(); } catch (_) {}
    }
  };

  // ---------- 💰 KNB: mở Đại Hồng Bao rồi gửi KNB thừa về nhân vật chính (thư tặng) / nhân vật chính nhận thư ----------
  // KNB thường chính xác (ô #knbTop viết gọn "17.2k"), lấy từ trạng thái game; không có thì đọc ô trên cùng
  const knbExact = () => { const S = gameGet('S'); return S && typeof S.knb === 'number' ? S.knb : ((knbNow() || {}).knb ?? null); };
  const knbFlowSend = async (arg) => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    const res = { r: 'ok', opened: 0, gained: 0, sent: 0, mails: 0, knb: 0 };
    if (arg.op === 'send' && !(arg.to && (arg.allowed || []).includes(arg.to))) return { ...res, r: 'not-allowed' };
    flowBusy = true;
    const stop = watchMsgs();
    try {
      if (arg.op === 'collect') {
        // Nhân vật chính: nhận thư chỉ có KNB từ nhân vật của bạn
        $('#u20mail')?.click();
        if (await waitFor(() => $('#mBody #nmTo') || $('#mBody .nmrow'), 3000)) {
          for (let i = 0, misses = 0; i < 20 && misses < 2; i++) {
            await sleep(500);
            const row = [...document.querySelectorAll('#mBody .nmrow')].find((r) =>
              (arg.from || []).includes(((r.querySelector(':scope > span > b') || {}).textContent || '').trim())
              && r.querySelector('[data-take]') && /KNB/.test(r.innerText) && !/khóa/.test(r.innerText));
            if (!row) break;
            const id = row.querySelector('[data-take]').dataset.take;
            row.querySelector('[data-take]').click();
            if (await waitFor(() => !$(`#mBody [data-take="${id}"]`), 3000)) { res.mails++; misses = 0; } else misses++;
          }
        }
      } else {
        // 1. Mở hết Đại Hồng Bao
        if (arg.openBig) {
          await openBag();
          const n = bigRedCount();
          closeBag();
          if (n > 0) {
            const before = knbExact();
            const got = await openBigRed(Infinity);
            if (got > 0) { res.opened = n; res.gained = (knbExact() ?? 0) - (before ?? 0) || got; }
          }
        }
        // 2. Gửi phần trên mức giữ lại về nhân vật chính
        const have = knbExact();
        const amount = have != null ? Math.floor(have - (arg.keep ?? 1000)) : 0;
        if (arg.op === 'send' && amount >= (arg.min ?? 1)) {
          await closeModalSure();
          $('#u20mail')?.click();
          const toBox = await waitFor(() => $('#mBody #nmTo'), 3000);
          if (toBox) {
            setValue(toBox, arg.to);
            const trade = $('#nmGdOn');
            if (trade && trade.checked) trade.click();
            if (/Đang chọn/.test(($('#nmPick') || {}).innerText || '')) { res.r = 'items-selected'; }
            else {
              setValue($('#nmGold'), String(amount));
              $('#nmSend').click();
              const yes = await waitFor(() => {
                const box = $('#askBox');
                if (!box || !isVisible(box)) return null;
                const t = box.innerText.replace(/\s+/g, ' ');
                if (!new RegExp('^Gửi [\\d.,]+ KNB cho ' + arg.to.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\?').test(t)) return 'bad';
                return [...box.querySelectorAll('button')].find((b) => b.dataset.a === '1' || /^Gửi$/.test(b.textContent.trim())) || 'bad';
              }, 3000);
              if (yes && yes !== 'bad') {
                yes.click();
                const after = await waitFor(() => { const x = knbExact(); return x != null && x <= have - amount ? x : null; }, 5000);
                res.sent = after != null ? have - after : 0;
                if (!after) res.r = 'not-sent';
              } else {
                res.r = 'no-confirm';
                const box = $('#askBox');
                if (box && isVisible(box)) { post({ t: 'log', msg: 'knb send: unexpected dialog ' + box.innerText.replace(/\s+/g, ' ').slice(0, 120) }); [...box.querySelectorAll('button')].find((b) => /^Hủy$/.test(b.textContent.trim()))?.click(); }
              }
            }
          }
        }
      }
      res.knb = knbExact() ?? 0;
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      stop();
      try { await closeModalSure(); closeBag(); } catch (_) {}
      flowBusy = false;
    }
    return res;
  };
  const guildShop = async (arg, menu, res) => {
    try {
      menu.click();
      if (!(await waitFor(() => $('#mBody [data-bht]'), 3000))) { res.r = 'no-window'; return; }
      if (!$('#mBody [data-bht="shop"]')) { res.r = 'no-guild'; return; }
      const showCat = async (cat) => {
        $('#mBody [data-bht="shop"]').click();
        if (!(await waitFor(() => $(`#mBody [data-bhsc="${cat}"]`), 2000))) return false;
        $(`#mBody [data-bhsc="${cat}"]`).click();
        return !!(await waitFor(() => $(`#mBody [data-bhmua][data-sl="1"]`) && guildPts() != null, 2000));
      };
      // Góp `amount` vạn; trả về số điểm sau khi góp (null nếu không được)
      const donate = async (amount) => {
        $('#mBody [data-bht="gop"]')?.click();
        const input = await waitFor(() => $('#bhGopV'), 2000), btn = $('#bhGopVb');
        if (!input || !btn) return null;
        const before = guildPts();
        setValue(input, String(amount));
        if (!(await pressAndAgree(btn, /góp|cống hiến/i)) && !(await waitFor(() => (guildPts() ?? 0) > (before ?? 0), 1500))) return null;
        return await waitFor(() => { const p = guildPts(); return p != null && p > (before ?? 0) ? p : null; }, 4000);
      };
      const donateInfo = async () => {
        $('#mBody [data-bht="gop"]')?.click();
        await waitFor(() => $('#bhGopV'), 2000);
        const card = $('#bhGopV')?.closest('.u2card');
        return card ? VLParse.guildDonate(card.innerText) : null;
      };

      for (const id of arg.items || []) {
        const cat = GUILD_CAT[id] || 'vp';
        if (!(await showCat(cat))) { res.r = 'no-shop'; break; }
        let s = guildState(id);
        if (!s) { reportUnreadable('guild-' + id, (guildBtn(id, 1)?.closest('.u2card') || $('#mBody') || {}).innerText || ''); continue; }
        if (s.locked || s.done >= s.max) continue;
        let points = guildPts() ?? 0;
        let want = s.max - s.done;
        // Thiếu điểm → góp vạn trong trần chi hôm nay
        if (points < want * s.price && (arg.donateCap || 0) > 0) {
          const info = await donateInfo();
          const room = info ? Math.min(arg.donateCap - info.done, info.cap - info.done, info.can ?? Infinity) : 0;
          const amount = Math.max(0, Math.min(want * s.price - points, room));
          if (amount > 0) {
            const after = await donate(amount);
            if (after != null) { res.donated += after - points; points = after; }
          }
          if (!(await showCat(cat))) { res.r = 'no-shop'; break; }
          s = guildState(id) || s;
        }
        want = Math.min(s.max - s.done, Math.floor(points / s.price));
        let misses = 0;
        while (want > 0 && misses < 3) {
          // Nút nhiều nhất không quá số cần (×10, ×5, ×6…), không có thì từng cái
          const sizes = [...document.querySelectorAll(`#mBody [data-bhmua="${id}"]`)].map((b) => +b.dataset.sl).filter((n) => n > 0 && n <= want);
          const sl = Math.max(1, ...sizes);
          const btn = guildBtn(id, sl);
          if (!btn || btn.disabled) break;
          const before = s.done;
          if (!(await pressAndAgree(btn, /điểm cống hiến/i))) { misses++; continue; }
          const now = await waitFor(() => { const x = guildState(id); return x && x.done > before ? x : null; }, 4000);
          if (!now) { misses++; continue; }
          const got = now.done - before;
          res.bought.push(`${id}:${got}`); res.spent += got * s.price;
          s = now; want -= got;
        }
      }
      res.points = guildPts();
      $('#mBody [data-bht="bang"]')?.click(); // để bảng về mục mặc định như lúc bạn mở
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      try { await closeModalSure(); } catch (_) {}
    }
  };
  // Nhận thư "Cửa hàng bang: …" (cả thư còn lại từ hôm trước)
  const guildMail = async (res) => {
    try {
      const mail = $('#u20mail');
      if (mail) {
        mail.click();
        if (await waitFor(() => $('#mBody .nmrow') || $('#mBody #nmAll'), 3000)) {
          // Nhận xong một thư game vẽ lại cả danh sách: chờ một nhịp rồi tìm lại từ đầu (nút cũ đã bị thay)
          for (let i = 0, misses = 0; i < 40 && misses < 3; i++) {
            await sleep(600);
            const row = [...document.querySelectorAll('#mBody .nmrow')].find((r) => /Cửa hàng bang:/.test(r.innerText) && r.querySelector('[data-take]'));
            if (!row) break;
            const id = row.querySelector('[data-take]').dataset.take;
            row.querySelector('[data-take]').click();
            if (isVisible($('#askBox'))) [...$('#askBox').querySelectorAll('button')].find((b) => b.dataset.a === '1')?.click();
            if (await waitFor(() => !$(`#mBody [data-take="${id}"]`), 4000)) res.mails++; else misses++;
          }
        }
        await closeModalSure();
      }
    } catch (err) {
      post({ t: 'log', msg: 'guild mail: ' + err.message });
      try { await closeModalSure(); } catch (_) {}
    }
  };

  // ---------- 🐴 Mã Trường: mở Rương Ngựa, cất loài mới, chuyển ngựa thừa giữa các acc của bạn ----------
  // Nút 🐴 (#u20stb) mở bảng Mã Trường trong #modal: thẻ ngựa đang cưỡi, bộ lọc, danh sách "Ngựa trong Mã Trường"
  // (thu gọn, nút data-hfo "▾ Hiện (N)"; mỗi dòng .qrow có Cưỡi / Lấy ra data-sout / Bán) và "Ngựa trong hành trang"
  // (Cưỡi / Cất vào Mã Trường data-sin / Bán), cuối cùng là bảng bộ sưu tập. Game tự đánh dấu "· trùng" và
  // "· trùng, giữ lại (tốt nhất)"; ngựa thừa = trùng mà game không giữ lại. Không bao giờ bấm Bán / Cho ăn (KNB).
  // Khoá / mở khoá ở thẻ món trong Hành trang (#bLock, miễn phí, không hỏi lại). Gửi thư ở 📨 Thư › 📤 Gửi thư:
  // #nmTo tên nhân vật, bấm món (data-uid) để kèm, #nmGdOn = thư giao dịch (người nhận trả KNB) → luôn tắt.
  const horseMsgs = [];
  const watchMsgs = () => {
    horseMsgs.length = 0;
    const mo = new MutationObserver((ms) => {
      for (const m of ms) for (const n of m.addedNodes) {
        const t = ((n.innerText || n.textContent || '') + '').trim();
        if (t && t.length < 200) horseMsgs.push(t);
      }
    });
    mo.observe(document.body, { childList: true, subtree: true });
    return () => mo.disconnect();
  };
  const sawMsg = (re) => horseMsgs.some((t) => re.test(t));
  const stableOpen = () => isVisible($('#modal')) && /^Mã Trường/.test(txt('#mBody h3'));
  const openStable = async () => {
    if (!stableOpen()) {
      await closeModalSure();
      const b = $('#u20stb');
      if (!b) return false;
      b.click();
      if (!(await waitFor(stableOpen, 3000))) return false;
    }
    // Bộ lọc về "Mọi bậc · Tất cả", bỏ "Chỉ ngựa trùng", mở danh sách trong Mã Trường
    for (const sel of ['[data-hf="t"][data-hv="-1"]', '[data-hf="w"][data-hv="all"]']) {
      const b = $('#mBody ' + sel);
      if (b && !b.classList.contains('on')) { b.click(); await sleep(300); }
    }
    const dup = $('#mBody [data-hf="dup"]');
    if (dup && dup.classList.contains('on')) { dup.click(); await sleep(300); }
    const show = $('#mBody [data-hfo]');
    if (show && /Hiện/.test(show.textContent)) {
      show.click();
      await waitFor(() => !/Hiện/.test((($('#mBody [data-hfo]') || {}).textContent) || ''), 1500);
    }
    return true;
  };
  const horseRows = () => [...document.querySelectorAll('#mBody .qrow')].map((r) => {
    const sin = r.querySelector('[data-sin]'), sout = r.querySelector('[data-sout]');
    if (!sin && !sout) return null;
    const h = VLParse.horseRow((r.innerText || '').split('\n')[0], r.innerText);
    return h && { ...h, uid: sin ? sin.dataset.sin : sout.dataset.sout, where: sin ? 'bag' : 'st', row: r };
  }).filter(Boolean);
  const stableNow = () => VLParse.stableCount(($('#mBody') || {}).innerText);
  const stableFull = () => { const s = stableNow(); return !!s && s.used >= s.cap; };
  // Bấm nút của một dòng (data-sin / data-sout) rồi chờ dòng đó đổi chỗ
  const moveHorse = async (uid, attr) => {
    const b = $(`#mBody [${attr}="${uid}"]`);
    if (!b) return false;
    b.click();
    const other = attr === 'data-sin' ? 'data-sout' : 'data-sin';
    return !!(await waitFor(() => $(`#mBody [${other}="${uid}"]`) || !$(`#mBody [${attr}="${uid}"]`), 3000));
  };
  // Mã Trường đầy: đưa một con thừa (trùng, không được giữ lại) từ Mã Trường ra Hành trang để lấy chỗ
  const makeStableRoom = async () => {
    const spare = horseRows().find((h) => h.where === 'st' && h.spare);
    return !!spare && (await moveHorse(spare.uid, 'data-sout')) && !stableFull();
  };
  // Cất mọi loài chưa có (ngựa trong Hành trang không bị đánh dấu trùng) vào Mã Trường
  const storeNewHorses = async (res) => {
    if (!(await openStable())) return;
    for (let i = 0; i < 30; i++) {
      const h = horseRows().find((x) => x.where === 'bag' && !x.dup);
      if (!h) break;
      if (stableFull() && !(await makeStableRoom())) { res.stableFull = true; break; }
      if (!(await moveHorse(h.uid, 'data-sin'))) break;
      res.stored.push(h.name);
      await sleep(300);
    }
  };
  // Hành trang đầy: cất tới `n` con ngựa từ Hành trang vào Mã Trường (loài mới trước, rồi con trùng) để có chỗ nhận thư.
  // Cất vài con một lượt vì đồ rơi khi luyện công nhặt vào chiếm ô trống rất nhanh. Trả về số ô đã dọn.
  const makeBagRoom = async (n = 3) => {
    if (!(await openStable())) return 0;
    let freed = 0;
    while (freed < n && !stableFull()) {
      const rows = horseRows().filter((h) => h.where === 'bag');
      const h = rows.find((x) => !x.dup) || rows.find((x) => x.dup);
      if (!h || !(await moveHorse(h.uid, 'data-sin'))) break;
      freed++;
    }
    return freed;
  };
  const chestCount = () => +((($('#t-inv [data-mi="rngua"] i') || {}).textContent) || 0) || 0;
  const openHorseChests = async (res) => {
    if (!(await openBag())) return;
    try {
      for (let i = 0, misses = 0; i < 30 && misses < 2; i++) {
        const have = chestCount();
        if (have <= 0) break;
        $('#t-inv [data-mi="rngua"]').click();
        const btn = await waitFor(() => { const b = $(have >= 10 ? '#miUse10' : '#miUse'); return b && isVisible(b) && !b.disabled ? b : null; }, 2000);
        if (!btn) { misses++; await closeModalSure(); continue; }
        horseMsgs.length = 0;
        btn.click();
        if (await waitFor(() => chestCount() < have, 5000)) { res.opened += have - chestCount(); misses = 0; }
        else if (sawMsg(/Hành trang đầy|đầy/)) { res.bagFull = true; break; } // ngựa ra không còn chỗ (Mã Trường và Hành trang đều đầy)
        else misses++;
        await closeModalSure();
      }
    } finally {
      await closeModalSure();
      closeBag();
    }
  };
  // Nhận thư có ngựa từ nhân vật của bạn (arg.from); Hành trang đầy thì cất bớt ngựa vào Mã Trường rồi nhận lại
  const HORSE_IMG = /\/i\/10_\d+\.png$/;
  const takeHorseMails = async (from, res) => {
    const mailOpen = async () => {
      if (!$('#mBody .nmrow') && !$('#mBody #nmTo')) {
        await closeModalSure();
        $('#u20mail')?.click();
      }
      return !!(await waitFor(() => $('#mBody #nmTo') || $('#mBody .nmrow'), 3000));
    };
    // Tên người gửi nằm ở <span><b>…</b>; ô món kèm theo cũng có thẻ <b> (vạch màu) nên không lấy <b> đầu tiên
    const ownRow = () => [...document.querySelectorAll('#mBody .nmrow')].find((r) =>
      from.includes(((r.querySelector(':scope > span > b') || {}).textContent || '').trim()) && r.querySelector('[data-take]')
      && [...r.querySelectorAll('img')].some((img) => HORSE_IMG.test(img.getAttribute('src') || '')));
    if (!(await mailOpen())) return;
    for (let i = 0, misses = 0, rooms = 0; i < 20 && misses < 2; i++) {
      // Danh sách vẽ lại sau mỗi lần nhận → chờ nhịp ngắn; vừa dọn chỗ thì bấm ngay
      if (!rooms || res.mails) await sleep(500);
      const row = await waitFor(ownRow, 1500);
      if (!row) break;
      const id = row.querySelector('[data-take]').dataset.take;
      horseMsgs.length = 0;
      row.querySelector('[data-take]').click();
      if (await waitFor(() => !$(`#mBody [data-take="${id}"]`), 2500)) { res.mails++; misses = 0; continue; }
      if (!sawMsg(/Hành trang đầy/)) { misses++; continue; }
      // Đầy: dọn chỗ rồi nhận ngay (đồ rơi khi luyện công có thể chiếm chỗ trống rất nhanh)
      if (++rooms > 5 || !(await makeBagRoom())) { res.bagFull = true; break; }
      if (!(await mailOpen())) break;
    }
    await closeModalSure();
  };
  const horseScan = () => {
    const coll = VLParse.horseCollection(($('#mBody') || {}).innerText);
    const spares = horseRows().filter((h) => h.spare).map(({ uid, key, name, where, locked }) => ({ uid, key, name, where, locked }));
    return { coll, spares, stable: stableNow() };
  };
  // Thẻ món trong Hành trang: mở / khoá (#bLock "🔓Mở khóa" khi đang khoá, "🔒Khóa" khi không)
  const setLocked = async (uid, lock) => {
    const it = $(`#t-inv [data-uid="${uid}"]`);
    if (!it) return false;
    it.click();
    const btn = await waitFor(() => { const b = $('#bLock'); return b && isVisible(b) ? b : null; }, 2000);
    let ok = false;
    if (btn) {
      const isLocked = /Mở khóa|Mở khoá/.test(btn.textContent);
      if (isLocked === lock) ok = true;
      else {
        btn.click();
        ok = !!(await waitFor(() => /Mở khóa|Mở khoá/.test((($('#bLock') || {}).textContent) || '') === lock, 2000));
      }
    }
    await closeModalSure();
    return ok;
  };
  // 💎 Góp KNB vào quỹ bang (bạn bật trong Cài đặt), mỗi ngày một lần: tới `amount` KNB trong ngày (tính cả phần đã
  // góp tay), không quá mức tối đa của game, luôn chừa `keep` KNB. Ô #bhGopK + nút #bhGopKb, game hỏi "Góp N KNB vào
  // quỹ bang …?". Trả về lý do khi không góp: enough (đã đủ hôm nay), low (không đủ KNB sau khi chừa), cap (hết mức).
  const guildKnbFlow = async (arg) => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    const load = gameGet('bhLoad');
    let d = null;
    try { if (typeof load === 'function') d = await load(); } catch (_) {}
    if (!d) return { r: 'no-data' };
    if (!d.co || !d.toi) return { r: 'no-guild' };
    const want = +arg.amount || 0, keep = arg.keep ?? 1000;
    const done = +d.toi.gop_knb || 0;
    const have = knbExact() ?? 0;
    if (done >= want) return { r: 'ok', given: 0, done, have, reason: 'enough' };
    const room = Math.max(0, (+d.toi.max_knb || 0) - done);
    const amount = Math.max(0, Math.min(want - done, room, have - keep));
    if (amount <= 0) return { r: 'ok', given: 0, done, have, reason: room <= 0 ? 'cap' : 'low' };
    flowBusy = true;
    try {
      if (!(await openGuildTab('gop'))) return { r: 'no-tab', done, have };
      const input = await waitFor(() => $('#bhGopK'), 2000), btn = $('#bhGopKb');
      if (!input || !btn || btn.disabled) return { r: 'no-button', done, have };
      setValue(input, String(amount));
      if (!(await pressAndAgree(btn, /KNB/i))) return { r: 'no-confirm', done, have };
      await sleep(800);
      let after = null;
      try { const d2 = await load(); after = d2 && d2.toi ? +d2.toi.gop_knb || 0 : null; } catch (_) {}
      return { r: 'ok', given: after != null ? Math.max(0, after - done) : amount, done: after ?? done + amount, have: knbExact() ?? have - amount };
    } finally {
      await closeModalSure();
      flowBusy = false;
    }
  };
  // 🏯 Sổ KNB của bang: tải dữ liệu bang bằng hàm của game (bhLoad → máy chủ "bh_get", không mở bảng nào) rồi gửi
  // nhật ký bang (30 dòng gần nhất), danh sách thành viên và quỹ về app
  const guildLogFlow = async () => {
    const load = gameGet('bhLoad');
    if (typeof load !== 'function') return { r: 'no-fn' };
    let d;
    try { d = await load(); } catch (e) { return { r: 'error: ' + (e && e.message || e) }; }
    if (!d || !d.co || !d.bang) return { r: 'no-guild' };
    const b = d.bang;
    return {
      r: 'ok',
      guild: { id: +b.id || 0, name: String(b.ten || ''), cap: +b.cap || 0 },
      fund: { knb: +b.quy_knb || 0, van: +b.quy_luong || 0, needKnb: +((b.nang_gia || {}).knb) || 0, needVan: +((b.nang_gia || {}).luong) || 0 },
      members: (d.tv || []).map((x) => ({ name: String(x.ten || ''), chuc: String(x.chuc || ''), lvl: +x.lvl || 0, week: +x.ch_tuan || 0, total: +x.ch_tong || 0, on: +x.on || 0 })),
      log: (d.log || []).map((x) => ({ at: +x.at || 0, msg: String(x.msg || '') })),
    };
  };
  // 🔒 Đồ có "Kỹ năng võ công vốn có" (thuộc tính allskill_v, mã 139) cực hiếm → khoá ngay (không bị Bán thừa, không
  // đốt Lò, không hợp Huyền Tinh). Chỉ đồ trong Hành trang của chính nhân vật.
  const hasInnate = (it) => { const JX = window.JX; return !!(JX && it && (it.mag || []).some((x) => JX.attr[x.a] === 'allskill_v')); };
  const innateUnlocked = () => { try { const S = window.H5.G('S'); return (S && S.inv || []).filter((i) => i && !i.lock && hasInnate(i)); } catch (_) { return []; } };
  // Đồ đang mặc có kỹ năng vốn có chưa khoá: [{slot, it}] (cởi ra sau này vẫn an toàn)
  const innateEquippedUnlocked = () => {
    try { const S = window.H5.G('S'); return Object.entries(S && S.eq || {}).filter(([, i]) => i && !i.lock && hasInnate(i)).map(([slot, it]) => ({ slot, it })); } catch (_) { return []; }
  };
  const lockInnateFlow = async () => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    const todo = innateUnlocked(), worn = innateEquippedUnlocked();
    if (!todo.length && !worn.length) return { r: 'ok', locked: '', failed: '' };
    flowBusy = true;
    const locked = [], failed = [];
    try {
      const desc = (it) => { const v = (it.mag || []).filter((x) => window.JX.attr[x.a] === 'allskill_v').map((x) => (x.p || [])[0]).join('+'); return `${it.n}${v ? ` (vốn có +${v})` : ''}`; };
      // Đồ đang mặc: gọi đúng việc nút "🔒 Khóa" trên thẻ món làm (H5.lockItem → máy chủ khoá), rồi đóng thẻ
      for (const { slot, it } of worn.slice(0, 10)) {
        try { window.H5.lockItem(it, slot); } catch (_) {}
        const ok = await waitFor(() => { const S = window.H5.G('S'); const cur = S && S.eq && S.eq[slot]; return cur && cur.lock; }, 4000);
        (ok ? locked : failed).push(desc(it) + ' · đang mặc');
        await closeModalSure();
      }
      if (todo.length && !(await openBag())) return { r: 'no-bag', locked: locked.join(', '), failed: failed.join(', ') };
      for (const it of todo.slice(0, 10)) {
        const lines = (it.mag || []).filter((x) => window.JX.attr[x.a] === 'allskill_v').map((x) => (x.p || [])[0]).join('+');
        ((await setLocked(it.uid, true)) ? locked : failed).push(`${it.n}${lines ? ` (vốn có +${lines})` : ''}`);
        await openBag();
      }
      if (todo.length) closeBag();
    } finally { flowBusy = false; }
    return { r: 'ok', locked: locked.join(', '), failed: failed.join(', ') };
  };
  // Gửi một con ngựa (đang ở Hành trang, không khoá) cho `to` qua thư tặng
  const mailHorse = async (to, uid, name) => {
    await closeModalSure();
    $('#u20mail')?.click();
    const toBox = await waitFor(() => $('#mBody #nmTo'), 3000);
    if (!toBox) return 'no-mail';
    setValue(toBox, to);
    const trade = $('#nmGdOn');
    if (trade && trade.checked) trade.click();
    const gold = $('#nmGold');
    if (gold && gold.value) setValue(gold, '');
    const item = $(`#mBody [data-uid="${uid}"]`);
    if (!item) return 'not-in-bag';
    item.click();
    if (!(await waitFor(() => new RegExp('Đang chọn:\\s*' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).test(($('#mBody') || {}).innerText), 2000))) return 'not-selected';
    if ($('#nmGdOn')?.checked || ($('#nmTo') || {}).value !== to) return 'form-changed';
    $('#nmSend')?.click();
    const ok = await waitFor(() => {
      const box = $('#askBox');
      if (!box || !isVisible(box)) return null;
      const t = box.innerText.replace(/\s+/g, ' ');
      if (!t.includes('Gửi ') || !t.includes(' cho ' + to + '?') || /KNB|lượng/.test(t)) return 'bad';
      return [...box.querySelectorAll('button')].find((b) => b.dataset.a === '1' || /^Gửi$/.test(b.textContent.trim()));
    }, 2500);
    if (!ok || ok === 'bad') {
      const box = $('#askBox');
      if (box && isVisible(box)) {
        post({ t: 'log', msg: 'horse mail: unexpected dialog ' + box.innerText.replace(/\s+/g, ' ').slice(0, 120) });
        [...box.querySelectorAll('button')].find((b) => /^Hủy$/.test(b.textContent.trim()))?.click();
      }
      return 'no-confirm';
    }
    ok.click();
    return (await waitFor(() => !$(`#mBody [data-uid="${uid}"]`), 4000)) ? 'ok' : 'not-sent';
  };
  // arg.op: 'scan' (nhận thư ngựa, mở rương, cất loài mới, đọc bộ sưu tập + ngựa thừa) ·
  // 'send' { to, uids, allowed } · 'take' { from } (nhận thư ngựa rồi cất loài mới)
  const horseFlow = async (arg) => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    if (!$('#u20stb')) return { r: 'no-stable' };
    // Chỉ gửi cho nhân vật của bạn
    if (arg.op === 'send' && !(arg.to && (arg.allowed || []).includes(arg.to))) return { r: 'not-allowed' };
    flowBusy = true;
    const stop = watchMsgs();
    const res = { r: 'ok', op: arg.op, opened: 0, stored: [], mails: 0, sent: [], failed: [], sold: [] };
    try {
      const from = arg.from || [];
      if (arg.op === 'scan' || arg.op === 'take') {
        if (from.length) await takeHorseMails(from, res);
        if (arg.op === 'scan' && arg.openChests) await openHorseChests(res);
        await storeNewHorses(res);
        if (await openStable()) Object.assign(res, horseScan());
      } else if (arg.op === 'sell') {
        // Bán từng con thừa đã được app chọn (không acc nào khác cần). Kiểm tra lại ngay trước khi bấm:
        // vẫn là con trùng game không giữ lại, không khoá. Game bán ngay, không hỏi lại (thấy hộp hỏi về KNB thì huỷ).
        for (const uid of arg.uids || []) {
          if (!(await openStable())) { res.failed.push(uid + ':no-stable'); continue; }
          const h = horseRows().find((x) => x.uid === uid);
          if (!h || !h.spare || h.locked) { res.failed.push(uid + ':not-spare'); continue; }
          const b = $(`#mBody [data-hsell="${uid}"]`);
          if (!b) { res.failed.push(uid + ':no-button'); continue; }
          b.click();
          const done = await waitFor(() => {
            if (!$(`#mBody [data-hsell="${uid}"]`)) return 'gone';
            const box = $('#askBox');
            return box && isVisible(box) ? 'ask' : null;
          }, 3000);
          if (done === 'ask') {
            const box = $('#askBox'), t = box.innerText.replace(/\s+/g, ' ');
            const yes = [...box.querySelectorAll('button')].find((x) => x.dataset.a === '1');
            if (/^Bán /.test(t) && !/KNB/.test(t) && yes) yes.click();
            else { post({ t: 'log', msg: 'horse sell: unexpected dialog ' + t.slice(0, 120) }); [...box.querySelectorAll('button')].find((x) => /^Hủy$/.test(x.textContent.trim()))?.click(); }
          }
          if (done === 'gone' || (await waitFor(() => !$(`#mBody [data-hsell="${uid}"]`), 2000))) res.sold.push(h.name);
          else res.failed.push(uid + ':not-sold');
        }
      } else if (arg.op === 'send') {
        const to = arg.to;
        // Con đang ở Hành trang gửi trước: mỗi con gửi đi trả lại một ô trống để lấy con trong Mã Trường ra
        const queue = [...(arg.uids || [])];
        if (await openStable()) {
          const where = Object.fromEntries(horseRows().map((x) => [x.uid, x.where]));
          queue.sort((a, b) => (where[a] === 'st') - (where[b] === 'st'));
        }
        const retry = [];
        for (let qi = 0; qi < queue.length; qi++) {
          const uid = queue[qi];
          if (!(await openStable())) { res.failed.push(uid + ':no-stable'); continue; }
          const h = horseRows().find((x) => x.uid === uid);
          if (!h || !h.spare) { res.failed.push(uid + ':gone'); continue; }
          if (h.where === 'st') {
            horseMsgs.length = 0;
            if (!(await moveHorse(uid, 'data-sout'))) {
              const full = sawMsg(/Hành trang đầy/);
              // Đầy: thử lại một lần sau khi các con khác đã gửi đi
              if (full && !retry.includes(uid)) { retry.push(uid); queue.push(uid); continue; }
              res.failed.push(uid + (full ? ':bag-full' : ':take-out')); continue;
            }
          }
          await closeModalSure();
          let unlocked = false;
          if (h.locked) {
            if (!(await openBag()) || !(await setLocked(uid, false))) { closeBag(); res.failed.push(uid + ':unlock'); continue; }
            unlocked = true;
            closeBag();
          }
          const r = await mailHorse(to, uid, h.name);
          await closeModalSure();
          if (r === 'ok') { res.sent.push(uid); continue; }
          res.failed.push(uid + ':' + r);
          // Không gửi được: khoá lại như cũ
          if (unlocked && (await openBag())) { await setLocked(uid, true); closeBag(); }
        }
      }
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      stop();
      try { await closeModalSure(); closeBag(); } catch (_) {}
      flowBusy = false;
    }
    res.stored = res.stored.join(',');
    res.sent = res.sent.join(',');
    res.failed = res.failed.join(',');
    res.sold = res.sold.join(',');
    return res;
  };

  // ---------- Tự nhận quà: 🎁 Phần thưởng › Điểm danh (mỗi ngày) và Mốc cấp ----------
  // Chạy đầu lượt bấm 📜 Nhiệm Vụ (trong buyLdtFlow). Nút còn nhận được thì không bị khoá (disabled);
  // đã nhận thì game đổi chữ thành "Đã nhận" và khoá lại.
  const claimGifts = async () => {
    const open = $('#giftBtn');
    if (!open) return;
    const claimed = [];
    try {
      open.click();
      if (!(await waitFor(() => $('#giftTabs'), 2000))) return;
      const tab = async (g) => { const b = $(`#giftTabs [data-g="${g}"]`); if (b) b.click(); await sleep(150); return !!b; };
      // Bấm một nút "Nhận" rồi chờ game khoá nút lại (đã nhận xong)
      const take = async (sel) => {
        const b = $(sel);
        if (!b || b.disabled || !/Nhận/.test(b.textContent) || /Đã nhận/.test(b.textContent)) return false;
        b.click();
        return !!(await waitFor(() => { const n = $(sel); return !n || n.disabled || /Đã nhận/.test(n.textContent); }, 4000));
      };
      if (await tab('login') && await take('#gLogin')) claimed.push('điểm danh hôm nay');
      if (await tab('lvms')) {
        for (const b of [...document.querySelectorAll('#mBody button[data-lv]')]) {
          const lv = b.dataset.lv;
          if (await take(`#mBody button[data-lv="${lv}"]`)) claimed.push(`mốc cấp ${lv}`);
        }
      }
      await tab('login'); // để bảng về mục mặc định như lúc bạn mở
    } finally {
      closeModal();
      await waitFor(() => !isVisible($('#modal')), 2000);
      if (claimed.length) post({ t: 'gifts', claimed: claimed.join('|') });
    }
  };

  // ---------- Sự kiện 🌹 Tri Ân Nhà Giáo: tặng Bó Hoa cho Thầy Đồ, ghép thêm khi sắp hết ----------
  // 🎁 Phần thưởng › thẻ "Tri Ân Nhà Giáo" (data-g="ng"). Đã thử thật trên game:
  //  - tặng: nút ×1 data-ng="du:bo:1" (KHÔNG bấm "Hết" = du:bo:1000, nút đó tặng sạch mọi Bó Hoa) tới khi "hôm nay x/40" đủ
  //    – quá 40 / ngày không được thêm điểm, kinh nghiệm thì đã đủ 3 tỷ;
  //  - ghép Bó Hoa ×1 (gh:bo:1) = 1 Thiệp + 3 Hoa Hồng + 1 Giấy Gói; Thiệp ×1 (gh:thiep:1) từ nguyên liệu nhặt được;
  //  - Giấy Gói +10 (gg:10) giá "10 vạn lượng" mỗi tờ – chỉ mua khi dòng giá ghi bằng lượng (VLParse.teacher.wrapPrice),
  //    trong trần chi; Lẵng Mây (100 KNB) và Lẵng Hoa không bao giờ đụng tới.
  // arg: { keep: số Bó Hoa giữ sẵn cho ngày mai (40), spendCap: trần chi Giấy Gói hôm nay (vạn lượng),
  //        exchange: đổi nguyên liệu thừa lấy quà }
  // Đổi nguyên liệu thừa: thứ tự ưu tiên (tên đúng như trên bảng)
  const TEACHER_EXCHANGE = ['Đại Hồng Bao', 'Tiên Thảo Lộ Đặc Biệt', 'Hồng Bao'];
  // Số nguyên liệu (mỗi loại) còn dư để đổi: chừa đủ cho số Bó Hoa còn tặng tới hết sự kiện (hôm nay còn cap − today,
  // mỗi ngày sau thêm cap), trừ Bó Hoa và Thiệp đã có; mỗi Bó Hoa = 1 Thiệp (1 Giấy + 1 Bút + 1 Mực) + 3 Hoa Hồng.
  // Không đọc được số ngày còn lại → 0 (không đổi).
  const teacherSpare = (s) => {
    if (!s.daysLeft || [s.paper, s.brush, s.ink].some((v) => v == null)) return 0;
    const gifts = s.cap * (s.daysLeft - 1) + Math.max(0, s.cap - s.today);
    const bouquets = Math.max(0, gifts - s.bouquets), cards = Math.max(0, bouquets - s.cards);
    return Math.max(0, Math.min(s.paper - cards, s.brush - cards, s.ink - cards, s.roses - 3 * bouquets));
  };
  // Nút "Đổi" của dòng "1 <name> <giá> mỗi loại …" (tên phải khớp hết: "Hồng Bao" ≠ "Đại Hồng Bao", "Tiên Thảo Lộ" ≠ "… Đặc Biệt")
  const exchangeButton = (name) => [...document.querySelectorAll('#mBody [data-ng^="doi:"]')].find((b) => {
    const row = ((b.closest('.qrow') || {}).innerText || '').replace(/\s+/g, ' ').trim();
    return row.startsWith(`1 ${name} `) && /^\d/.test(row.slice(name.length + 3));
  });
  const teacherFlow = async (arg) => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    const open = $('#giftBtn');
    if (!open) return { r: 'no-event' };
    flowBusy = true;
    const res = { r: 'ok', gifted: 0, crafted: 0, cards: 0, wrapsBought: 0, spent: 0 };
    const st = () => VLParse.teacher(($('#mBody') || {}).innerText);
    // Bấm nút `sel` một lần rồi chờ số `key` tăng; trả về trạng thái mới hoặc null
    const press = async (sel, key) => {
      const s = st(), b = $(`#mBody [data-ng="${sel}"]`);
      if (!s || !b || b.disabled) return null;
      b.click();
      const n = await waitFor(() => { const x = st(); return x && x[key] > s[key] ? x : null; });
      if (isVisible($('#askBox'))) { post({ t: 'log', msg: 'teacher: unexpected dialog ' + $('#askBox').innerText.replace(/\s+/g, ' ').slice(0, 120) }); return null; }
      return n;
    };
    try {
      open.click();
      if (!(await waitFor(() => $('#giftTabs'), 2000))) return { ...res, r: 'no-event' };
      const tab = $('#giftTabs [data-g="ng"]');
      if (!tab) return { ...res, r: 'no-event' };
      tab.click();
      let s = await waitFor(st, 3000);
      if (!s) { reportUnreadable('teacher', ($('#mBody') || {}).innerText); return { ...res, r: 'unreadable' }; }

      // 1. Tặng từng Bó Hoa tới đủ điểm hôm nay
      for (let i = 0; i < 100 && s.today < s.cap && s.bouquets > 0; i++) {
        const n = await press('du:bo:1', 'today');
        if (!n) { res.r = 'gift-stuck'; break; }
        s = n; res.gifted++;
      }

      // 2. Giữ sẵn `keep` Bó Hoa cho ngày mai: ghép thêm (mua Giấy Gói bằng lượng nếu thiếu, trong trần chi)
      const keep = arg.keep || 40;
      let budget = Math.max(0, arg.spendCap || 0);
      for (let i = 0; i < 200 && res.r === 'ok' && s.bouquets < keep; i++) {
        if (s.roses < 3) break;
        if (s.wraps < 1) {
          if (s.wrapPrice == null || s.wrapPrice * 10 > budget) break; // giá không bằng lượng / hết trần chi → thôi
          const n = await press('gg:10', 'wraps');
          if (!n) { res.r = 'wrap-stuck'; break; }
          res.wrapsBought += n.wraps - s.wraps; res.spent += s.wrapPrice * (n.wraps - s.wraps); budget -= s.wrapPrice * 10;
          s = n;
          continue;
        }
        if (s.cards < 1) {
          const n = await press('gh:thiep:1', 'cards');
          if (!n) break; // hết nguyên liệu ghép Thiệp
          s = n; res.cards++;
          continue;
        }
        const n = await press('gh:bo:1', 'bouquets');
        if (!n) { res.r = 'craft-stuck'; break; }
        s = n; res.crafted++;
      }
      // 3. Đổi nguyên liệu thừa (arg.exchange): Đại Hồng Bao → Tiên Thảo Lộ Đặc Biệt → Hồng Bao, chỉ đổi phần DƯ sau khi
      //    đã chừa đủ nguyên liệu tặng Bó Hoa mỗi ngày tới hết sự kiện (teacherSpare)
      const exchanged = [];
      for (let i = 0; arg.exchange && res.r === 'ok' && i < 20; i++) {
        const spare = teacherSpare(s);
        const pick = TEACHER_EXCHANGE.find((name) => {
          const e = s.exchanges.find((x) => x.name === name);
          return e && e.done < e.max && e.cost <= spare;
        });
        if (!pick) break;
        const before = s.exchanges.find((x) => x.name === pick).done;
        const b = exchangeButton(pick);
        if (!b || b.disabled) { res.r = 'exchange-stuck'; break; }
        b.click();
        const n = await waitFor(() => { const x = st(); const e = x && x.exchanges.find((y) => y.name === pick); return e && e.done > before ? x : null; });
        if (!n || isVisible($('#askBox'))) { res.r = 'exchange-stuck'; break; }
        s = n; exchanged.push(pick);
      }
      res.exchanged = exchanged.join('|');
      Object.assign(res, { points: s.points, today: s.today, cap: s.cap, bouquets: s.bouquets, endsOn: s.endsOn });
      $('#giftTabs [data-g="login"]')?.click(); // để bảng về mục mặc định như lúc bạn mở
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      try { await closeModalSure(); } catch (_) {}
      flowBusy = false;
    }
    return res;
  };

  // want = số lệnh bài muốn mua (app tính: đủ dùng hết lượt lệnh bài hôm nay); keepKnb = KNB thường luôn giữ lại
  const buyLdtFlow = async (arg) => {
    if (anyWindowOpen() || flowBusy) return { r: 'busy' };
    flowBusy = true;
    const res = { r: 'ok', bought: 0, converted: 0, opened: 0, gained: 0, spent: 0 };
    const keep = arg.keepKnb || 0;
    let left = 0;
    try {
      try { await claimGifts(); } catch (err) { post({ t: 'log', msg: 'claim gifts failed: ' + err.message }); }
      if (!(await openShop()) || !(await showLdtTab())) {
        reportUnreadable('shop', (ldtCard() || {}).innerText || '');
        return { ...res, r: 'no-shop' };
      }
      const st0 = ldtState();
      left = Math.min(arg.want || 0, st0.max - st0.bought);
      for (let round = 0; left > 0 && round < 20; round++) {
        const k = knbNow();
        if (!k) { res.r = 'knb-unreadable'; reportUnreadable('knb', txt('#knbTop')); break; }
        const canBuy = Math.floor(k.khoa / LDT_PRICE);
        if (canBuy < left) {
          // Thiếu KNB khóa → đổi KNB thường (làm tròn lên bội số 10, không đụng phần giữ lại)
          const need = Math.ceil((left * LDT_PRICE - k.khoa) / 10) * 10;
          const usable = Math.floor(Math.max(0, k.knb - keep) / 10) * 10;
          const amount = Math.min(need, usable);
          if (amount >= 10) {
            const c = await convertKnb(amount);
            res.converted += c;
            await showLdtTab();
            if (c > 0) continue;
          }
          if (amount < need && bigRedCount() > 0 && res.opened < 10) {
            // Hết KNB thường → mở 1 Đại Hồng Bao rồi tính lại
            const got = await openBigRed();
            if (got > 0) { res.opened++; res.gained += got; }
            if (!(await openShop()) || !(await showLdtTab())) { res.r = 'no-shop'; break; }
            if (got > 0) continue;
          }
          if (canBuy === 0) { res.r = 'short'; break; }
        }
        const k1 = knbNow();
        const n = await buyLdt(Math.min(canBuy, left));
        const k2 = knbNow();
        res.bought += n; left -= n;
        if (k1 && k2) res.spent += k1.khoa - k2.khoa;
        const after = ldtState();
        if (after && after.bought >= after.max) left = 0; // đã mua đủ trong ngày (kể cả lượt mua chưa kịp tính)
        if (n === 0 && left > 0) { res.r = 'stuck'; break; }
      }
      const st = ldtState();
      if (st) { res.boughtToday = `${st.bought}/${st.max}`; res.have = st.have; quests.ldtBuy = res.boughtToday; quests.ldtHave = String(st.have); }
      if (left > 0 && res.r === 'ok') res.r = 'short';
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      res.left = Math.max(0, left);
      const first = $('#mBody [data-kb="0"]'); if (first && shopOpen()) first.click();
      try { await closeModalSure(); } catch (_) {}
      flowBusy = false;
    }
    return res;
  };

  wv.addEventListener('message', (e) => {
    const m = e.data || {};
    if (m.t === 'capture') {
      capturing = !!m.on;
    } else if (m.t === 'replay') {
      enqueue(m.ev || {});
    } else if (m.t === 'keepSys') {
      keepSys = !!m.on; // tắt khi bạn đang xem / dùng khung này (để bạn tự chọn kênh chat)
    } else if (m.t === 'teacher') {
      teacherFlow(m.arg || {}).then((res) => post({ t: 'teacherResult', ...res }), (err) => post({ t: 'teacherResult', r: 'error: ' + err.message }));
    } else if (m.t === 'knbBuy') {
      knbFlow().then((res) => post({ t: 'knbResult', ...res }), (err) => post({ t: 'knbResult', r: 'error: ' + err.message }));
    } else if (m.t === 'guild') {
      guildFlow(m.arg || {}).then((res) => post({ t: 'guildResult', ...res }), (err) => post({ t: 'guildResult', r: 'error: ' + err.message }));
    } else if (m.t === 'kiteCfg') {
      kiteCfg = m.arg || null; // {on, step, pauseMs, crowd, near, span, margin, fleeHp}
    } else if (m.t === 'knbSend') {
      knbFlowSend(m.arg || {}).then((res) => post({ t: 'knbSendResult', ...res }), (err) => post({ t: 'knbSendResult', r: 'error: ' + err.message }));
    } else if (m.t === 'bossHk') {
      bossHkFlow(m.arg || {}).then((res) => post({ t: 'bossHkResult', ...res }), (err) => post({ t: 'bossHkResult', r: 'error: ' + err.message }));
    } else if (m.t === 'guildKnb') {
      guildKnbFlow(m.arg || {}).then((res) => post({ t: 'guildKnbResult', ...res }), (err) => post({ t: 'guildKnbResult', r: 'error: ' + err.message }));
    } else if (m.t === 'guildLog') {
      guildLogFlow().then((res) => post({ t: 'guildLogResult', ...res }), (err) => post({ t: 'guildLogResult', r: 'error: ' + err.message }));
    } else if (m.t === 'lockInnate') {
      lockInnateFlow().then((res) => post({ t: 'lockInnateResult', ...res }), (err) => post({ t: 'lockInnateResult', r: 'error: ' + err.message }));
    } else if (m.t === 'sellJunk') {
      sellJunkFlow().then((res) => post({ t: 'sellJunkResult', ...res }), (err) => post({ t: 'sellJunkResult', r: 'error: ' + err.message }));
    } else if (m.t === 'horse') {
      horseFlow(m.arg || {}).then((res) => post({ t: 'horseResult', ...res }), (err) => post({ t: 'horseResult', r: 'error: ' + err.message }));
    } else if (m.t === 'buffs') {
      buffFlow(m.arg || {}).then((res) => post({ t: 'buffResult', ...res }), (err) => post({ t: 'buffResult', r: 'error: ' + err.message }));
    } else if (m.t === 'buyLdt') {
      buyLdtFlow(m.arg || {}).then((res) => post({ t: 'ldtResult', ...res }), (err) => post({ t: 'ldtResult', r: 'error: ' + err.message }));
    } else if (m.t === 'cmd' && commands[m.name]) {
      let result;
      try { result = commands[m.name](m.arg); } catch (err) { result = 'error: ' + err.message; }
      // Không gửi lại mật khẩu trong kết quả. Lệnh chạy lâu (async) thì gửi khi xong.
      const arg = m.name === 'login' ? '' : (m.arg || '');
      Promise.resolve(result)
        .then((r) => post({ t: 'cmdResult', name: m.name, arg, result: r }))
        .catch((err) => post({ t: 'cmdResult', name: m.name, arg, result: 'error: ' + err.message }));
    }
  });

  // ---------- Số lượt Dã Tẩu / Phó bản ----------
  // Chỉ có trong bảng Nhiệm vụ, và game chỉ dựng bảng khi mở. Mở → đọc → đóng trong cùng một
  // lượt chạy JS nên trình duyệt không kịp vẽ, người chơi không thấy bảng nháy lên.
  // Đang mở bảng nào đó (người chơi đang dùng) thì không đụng vào, chỉ đọc nếu bảng Nhiệm vụ đang hiện sẵn.
  const QUEST_REFRESH_MS = 60000;
  let quests = { dt: '', pb: '', ldtHave: '', ldtUsed: '', dtMax: '' }, questsAt = 0, gameSince = 0;

  // Thẻ Dã Tẩu: "Hôm nay 52/52 (+12 lệnh bài)", "Lệnh Bài Dã Tẩu: có 22 · … · đã dùng 35/160 (tối đa 200 …)" (xem VLParse.daTau).
  // Chỉ đọc khi đúng thẻ Dã Tẩu đang hiện.
  const readQuestTab = (tab) => {
    const sel = tab.querySelector('[data-qt].on');
    if (sel && sel.dataset.qt !== 'dt') return {};
    return VLParse.daTau((tab.querySelector('.u2qd') || tab).innerText);
  };

  // Chỉ ghi đè giá trị đọc được (bảng đang ở mục khác thì một số dòng không có)
  const mergeQuest = (r) => { for (const [k, v] of Object.entries(r)) if (v) quests[k] = v; };

  // Kỳ Trân Các › "Ủy thác & nhiệm vụ" › Lệnh Bài Dã Tẩu: "Hôm nay 55/100 · có 21" (đã mua / tối đa mỗi ngày).
  // Mở → chuyển tab → đọc → về tab đầu → đóng trong cùng một lượt JS: không nháy. Đang mở bảng/cửa sổ nào thì bỏ qua.
  const SHOP_REFRESH_MS = 120000;
  let shopAt = 0;
  const refreshShop = () => {
    const btn = $('#u20ktc'), panel = $('#panel'), modal = $('#modal');
    if (!btn || anyWindowOpen()) return;
    btn.click();
    try {
      const body = $('#mBody');
      if (!body) return;
      const tabs = [...body.querySelectorAll('button')];
      const taskTab = tabs.find((b) => /Ủy thác/i.test(b.textContent));
      if (taskTab) taskTab.click();
      const card = body.querySelector('[data-ktc="ldt"]')?.closest('.u2card');
      const shop = card ? VLParse.ldtShop(card.querySelector('.kc-s')?.textContent) : null;
      if (shop) quests.ldtBuy = `${shop.bought}/${shop.max}`;
      else if (card) reportUnreadable('shop', card.innerText);
      const first = tabs.find((b) => /Bảng KNB khóa/i.test(b.textContent));
      if (first) first.click();
    } finally {
      const close = $('#mClose');
      if (close && isVisible(close)) close.click();
    }
  };

  // Hòm đồ: số ô đã dùng / tổng ("18/120"), chỉ có khi mở bảng Hòm đồ. Mở → đọc → đóng trong một lượt JS
  // như bảng Nhiệm vụ; đang mở bảng nào thì chỉ đọc nếu đúng bảng Hòm đồ đang hiện.
  const BAG_REFRESH_MS = 120000;
  let bag = '', bagAt = 0;
  const readBag = (tab) => {
    const el = [...tab.querySelectorAll('span, b, div')].find((e) => e.children.length === 0 && /^\d+\s*\/\s*\d+$/.test(e.textContent.trim()));
    return el ? el.textContent.replace(/\s+/g, '') : '';
  };
  // ---------- Đồ phổ đang giữ (Hành trang + Rương Chứa Đồ) ----------
  // Tên món chỉ có trong tooltip khi rê chuột → rê chuột giả lập lên từng món (game vẽ tooltip ngay, không nháy).
  // Mỗi món (data-uid, không đổi) chỉ đọc một lần rồi nhớ lại. Đồ phổ không xếp chồng nên luôn có data-uid.
  const DOPHO_RE = /Đồ\s*phổ/i;
  const itemInfo = new Map(); // uid → tên Đồ phổ, hoặc '' nếu không phải
  let doPho = null;           // tên các Đồ phổ đang giữ, ví dụ ["Ngự Long Lượng Ngân Bảo Đao (rương)"]; null = chưa quét
  // Rương chỉ đổi khi bạn tự cất đồ → đọc một lần mỗi lần vào game (tải trang); không mở được thì 5 phút sau thử lại
  const CHEST_RETRY_MS = 5 * 60000;
  let chestDoPho = null, chestAt = 0, chestBroken = false;
  // Chú thích của chính món đó: { name, text }. Đồ mặc được có 2 khung trong #u2tip: ".u2pane.u2cur" = "Đang trang bị"
  // (để so sánh, bỏ qua) và khung của món đang rê chuột. Món không mặc được chỉ có một khung.
  const tipOf = (el) => {
    const r = el.getBoundingClientRect();
    for (const t of ['pointerover', 'mouseover', 'mouseenter', 'mousemove'])
      el.dispatchEvent(new MouseEvent(t, { bubbles: t !== 'mouseenter', clientX: r.left + 5, clientY: r.top + 5 }));
    const tip = $('#u2tip');
    let res = null;
    if (tip && isVisible(tip)) {
      const panes = [...tip.querySelectorAll('.u2pane')];
      const own = panes.length ? panes.find((p) => !p.classList.contains('u2cur')) : tip;
      if (own) res = { name: ((own.querySelector('h4') || {}).textContent || '').replace(/\s*\+\d+\s*$/, '').trim(), text: own.innerText.replace(/\s+/g, ' ').trim() };
    }
    for (const t of ['pointerout', 'mouseout', 'mouseleave']) el.dispatchEvent(new MouseEvent(t, { bubbles: t !== 'mouseleave' }));
    return res;
  };
  // Đồ phổ: biểu tượng riêng (img/ep/dopho.png) và chú thích "Đồ phổ <tên> · Gói mảnh · cấp N".
  // (Đồ Hoàng Kim Môn Phái ghép từ Đồ phổ không phải Đồ phổ.)
  const hasDoPhoIcon = (el) => !!el.querySelector('img[src*="dopho"]');
  const isDoPho = (el, t) => hasDoPhoIcon(el) || DOPHO_RE.test(t.text);
  const doPhoName = (t) => (t.name || t.text.split(/\s(?:Gói mảnh|·)/)[0]).replace(/^\s*Đồ\s*phổ[:\s]*/i, '').trim().slice(0, 60);
  const scanDoPho = (items, where) => {
    const found = [];
    for (const el of items) {
      const uid = el.dataset.uid;
      if (!uid) continue;
      if (!itemInfo.has(uid)) {
        const tip = tipOf(el);
        if (!tip || !tip.text) {
          // chưa đọc được chú thích: thấy biểu tượng Đồ phổ thì vẫn đếm, chưa nhớ tên (lần sau đọc lại)
          if (hasDoPhoIcon(el)) found.push(`Đồ phổ (${where})`);
          continue;
        }
        const name = isDoPho(el, tip) ? doPhoName(tip) || 'Đồ phổ' : '';
        if (itemInfo.size > 2000) itemInfo.clear();
        itemInfo.set(uid, name);
      }
      const name = itemInfo.get(uid);
      if (name) found.push(`${name} (${where})`);
    }
    return found;
  };

  // Hành trang là cửa sổ riêng (#u2bagw, đóng bằng #u2bwx), không nằm trong bảng #panel
  const refreshBag = () => {
    const win = $('#u2bagw'), tab = $('#t-inv'), btn = $('#u20menu > button[data-u="inv"]');
    if (!tab || !btn) return;
    if (isOpen(win)) { bag = readBag(tab) || bag; return; } // bạn đang mở: chỉ đọc
    if (anyWindowOpen()) return;
    btn.click();
    try {
      bag = readBag(tab) || bag;
      const found = scanDoPho([...tab.querySelectorAll('button.it[data-uid]')], 'hành trang');
      // Rương: chỉ mở lần đầu sau khi vào game; các lần sau dùng lại kết quả (Đồ phổ mới nhặt nằm ở Hành trang)
      const stash = $('#bStash');
      if (stash && !chestBroken && chestDoPho === null && Date.now() - chestAt > CHEST_RETRY_MS) {
        chestAt = Date.now();
        stash.click();
        // Hai kiểu giao diện:
        //  - màn rộng (Desktop): cửa sổ riêng #u2chw, game chỉ tạo khi mở (đóng là xoá) → lấy sau khi bấm
        //  - màn hẹp (bản Server 640×360): hộp thoại chung #modal "📦 Rương Chứa Đồ", đóng bằng #mClose
        const chestModal = () => { const m = $('#modal'); return !!m && isVisible(m) && /Rương Chứa Đồ/.test(($('#mBody') || {}).textContent || ''); };
        const chestOpen = () => isOpen($('#u2chw')) || chestModal();
        try {
          if (isOpen($('#u2chw'))) chestDoPho = scanDoPho([...document.querySelectorAll('#u2chg button.it[data-uid]')], 'rương');
          else if (chestModal()) chestDoPho = scanDoPho([...document.querySelectorAll('#mBody button.it[data-uid]')], 'rương');
        } finally {
          const cx = $('#u2chx'); if (isOpen($('#u2chw')) && cx) cx.click();
          if (chestModal()) closeModal();
          // Mở rồi mà không đóng được → thôi không mở Rương nữa (tới khi tải lại trang), báo app
          if (chestOpen()) {
            chestBroken = true;
            post({ t: 'log', msg: 'chest window did not close – chest scan disabled' });
            post({ t: 'problem', key: 'chest', msg: 'Không đóng được cửa sổ Rương sau khi đếm Đồ phổ – đã ngừng mở Rương, hãy tự đóng nó (📺)' });
          }
        }
      }
      // Hành trang là nguồn chính (đồ rơi ra luôn vào Hành trang); Rương thêm vào nếu đã đọc được
      doPho = [...found, ...(chestDoPho || [])];
    } finally {
      // Lấy lại cửa sổ sau khi bấm: game chỉ tạo cửa sổ Hành trang ở lần mở đầu tiên sau khi tải trang
      const close = $('#u2bwx');
      if (isOpen($('#u2bagw')) && close) close.click();
    }
  };

  // Đang đánh Sát Thủ: góc nhiệm vụ theo dấu có dòng "Sát Thủ · Truy sát mục tiêu"
  // Nút "Đi đến Boss Sát Thủ" (game hiện sau khi gọi boss, cùng nút "Hủy bỏ nhiệm vụ"). Chỉ nhận nút có chữ
  // "Đi đến"/"Đi tới"/"Tới"/"Dịch chuyển"; nút nào có "Hủy"/"Bỏ" thì tuyệt đối bỏ qua để không mất lệnh bài.
  // Game đã đổi chữ nút: "Đến chỗ Sát Thủ" (cạnh "Bỏ truy sát" – nút đó có chữ "Bỏ" nên không bao giờ bị bấm)
  const GO_RE = /(Đi\s*(đến|tới)|Đến\s*chỗ|Tới\s*(nơi|boss|chỗ)|Dịch\s*chuyển|Truy\s*sát)/i, CANCEL_RE = /Hủy|Huỷ|Bỏ/i;
  const assassinGoButton = () => [...document.querySelectorAll('#askBox button, #modal button, #panel button, #u20trk button, #u20trk a')]
    .find((b) => isVisible(b) && !b.disabled && GO_RE.test(b.textContent) && !CANCEL_RE.test(b.textContent)) || null;
  // Chữ các nút đang hiện (để ghi log khi không tìm thấy nút Đi đến)
  const assassinButtonsText = () => [...document.querySelectorAll('#askBox button, #modal button, #t-quest button:not([data-st]):not([data-qt])')]
    .filter(isVisible).map((b) => b.textContent.trim().slice(0, 30)).join(' | ').slice(0, 200);
  // Chỉ đóng bảng Nhiệm vụ; không đụng tới hộp hỏi của game (trong đó có nút "Hủy bỏ nhiệm vụ")
  const closeQuestWindows = () => { const x = $('#u20wx'); if (x && isVisible($('#panel'))) x.click(); };

  // (đọc cả khi khung nhỏ ẩn góc nhiệm vụ)
  // Truy sát đang mở (Sát Thủ hoặc Boss Hoàng Kim từ Lệnh Bài): trạng thái game S.st.cur {n, hpf}; không đọc được thì
  // dựa vào góc nhiệm vụ theo dấu. Còn truy sát thì game khoá mọi nút gọi mới ("Cấp 20…90").
  const huntNow = () => {
    try { const S = window.H5 && window.H5.G ? window.H5.G('S') : null; const c = S && S.st && S.st.cur; return c ? { n: String(c.n || ''), hpf: +c.hpf || 0 } : null; } catch (_) { return null; }
  };
  // S.gb = {key: "2026-11-08#1" (null ngoài đợt), killed, hpf, n, zn, end, next}; đợt 08/11 15:30: acc hạ xong thì
  // killed = true, acc chưa đánh thì hpf vẫn 1 dù acc khác đã hạ
  // Chế độ thật trong game: S.mode ("farm" / "tower" / …), đang trong tháp (R.tower), phó bản (R.dg)
  const gameModeNow = () => {
    try { const S = window.H5.G('S'), R = window.H5.G('R'); return S ? { m: String(S.mode || ''), tower: !!(R && R.tower), dg: !!(R && R.dg) } : null; } catch (_) { return null; }
  };
  // 08/11 22:07: 2 nhân vật chết trong tháp, game đưa ra Luyện Công (S.mode = farm) nhưng nút "🗼 Leo Tháp"
  // vẫn sáng → app tưởng đang leo tháp, không đưa vào lại suốt 25 phút. Nút "Leo Tháp" sáng mà game không ở tháp
  // (hoặc ngược lại) quá 5 giây → báo theo game.
  let modeMismatchSince = 0;
  const reportedMode = (btn) => {
    const gm = gameModeNow();
    let real = null;
    if (gm && !gm.dg) {
      if (btn === 'tower' && !gm.tower && gm.m && gm.m !== 'tower') real = gm.m;
      else if (btn && btn !== 'tower' && gm.tower) real = 'tower';
    }
    if (!real) { modeMismatchSince = 0; return btn; }
    if (!modeMismatchSince) modeMismatchSince = Date.now();
    return Date.now() - modeMismatchSince > 5000 ? real : btn;
  };
  // 📜 Nhật ký game: mọi dòng đi qua hàm log của game (R.logs.unshift, giữ 150 dòng) → bắt ngay lúc ghi, không phụ
  // thuộc thẻ chat đang mở, không trôi. Mỗi dòng kèm nơi đang đứng ("Tầng N" trong tháp, hoặc tên bản đồ).
  const gameLogQ = [];
  // Dòng về người chơi khác: thông báo chung của server ("📢 Chúc mừng X nhận được …"), kênh thế giới / bang / tổ đội…
  // → không ghi. Riêng thông báo có tên chính nhân vật này thì giữ.
  const OTHERS_RE = /Chúc mừng|📢|Hệ thống\s*:|Thế giới|tổ đội|Tổ đội|gia nhập bang|rời bang|\[Bang\]|\[Đội\]/;
  const aboutOthers = (text) => {
    if (!OTHERS_RE.test(text)) return false;
    let me = ''; try { me = String(window.H5.G('S').name || ''); } catch (_) {}
    return !(me && text.includes(me));
  };
  let lastCleared = null;
  const logPlace = () => {
    try {
      const R = window.H5.G('R');
      if (R && R.tower && R.tower.floor) return `Tầng ${R.tower.floor}`;
      if (R && R.dg) return 'Phó bản';
    } catch (_) {}
    return (txt('#u20zone') || '').replace(/[▾\s]+$/, '').trim();
  };
  const logText = (it) => {
    const raw = typeof it === 'string' ? it : it && (it.html || it.t || it.text) || '';
    return String(raw).replace(/<[^>]+>/g, '').replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim().slice(0, 300);
  };
  const hookGameLog = () => {
    try {
      const R = window.H5 && window.H5.G && window.H5.G('R');
      const L = R && R.logs;
      if (!Array.isArray(L) || L.__vlHook) return;
      const orig = L.unshift;
      Object.defineProperty(L, '__vlHook', { value: true });
      L.unshift = function (...items) {
        try {
          const now = Date.now();
          for (const it of items) {
            const text = logText(it);
            if (!text) continue;
            // Qua tầng N: game tăng số tầng ngay, đồ của tầng N được nhặt sau đó vài giây → vẫn ghi là tầng N
            if (aboutOthers(text)) continue; // chỉ ghi chuyện của chính nhân vật này
            const cleared = text.match(/qua tầng (\d+)/);
            if (cleared) lastCleared = { floor: +cleared[1], at: now };
            const place = /^Nhặt được/.test(text) && lastCleared && now - lastCleared.at < 4000 ? `Tầng ${lastCleared.floor}` : logPlace();
            noteLogEvent(text, place, now);
            gameLogQ.push({ at: now, text, place });
          }
          if (gameLogQ.length > 500) gameLogQ.splice(0, gameLogQ.length - 500);
        } catch (_) {}
        return orig.apply(this, items);
      };
    } catch (_) {}
  };
  // 💎 Đồ hiếm vào túi: so danh sách đồ của nhân vật (H5.st.items – mọi chỗ: túi, đang mặc, rương, Mã Trường) với lần
  // trước; món mới là HK bộ chung / HK Môn Phái (yêu cầu môn phái), Đồ phổ, đồ có kỹ năng vốn có, Vô Danh → một dòng
  // "💎 HK Môn Phái: <tên> ← <nguồn>". Nguồn = việc vừa xảy ra trong 10 giây (hạ boss, mốc tầng, Dã Tẩu #, mở rương…),
  // không có thì "nhặt ở <bản đồ>". Mốc 100 tầng cho HK thẳng vào túi, game không ghi dòng nào – chỉ thấy được cách này.
  let lastEvent = null;
  const recentPickup = new Map(); // tên món → lúc game ghi "Nhặt được <tên>"
  const noteLogEvent = (text, place, now) => {
    let m, src = null;
    if ((m = text.match(/^👑 Hạ Boss Hoàng Kim (.+?)!?$/))) src = `Hạ Boss Hoàng Kim ${m[1]}`;
    else if ((m = text.match(/^🗡 Hạ Sát Thủ · (.+?)!?$/))) src = `Hạ Sát Thủ ${m[1]}`;
    else if (/Boss tuần/.test(text)) src = 'Boss tuần';
    else if ((m = text.match(/qua tầng (\d+)/))) src = +m[1] % 100 === 0 ? `Mốc tầng ${m[1]}` : `Tháp tầng ${m[1]}`;
    else if ((m = text.match(/^🎁 (Mốc )?Dã Tẩu (#?\d+)/))) src = `${m[1] ? 'Mốc ' : ''}Dã Tẩu ${m[2]}`;
    else if (/^🎁 Mở .*Rương|Mở \d+ Rương/.test(text)) src = text.replace(/^🎁 /, '').slice(0, 60);
    if (src) lastEvent = { text: src, place, at: now };
    if ((m = text.match(/^Nhặt được (.+)$/))) recentPickup.set(m[1].trim(), now);
  };
  let bagKnownIds = null, bagKnownChar = '';
  const rareLabel = (it) => {
    if (it.set && it.set.kind === 'gold') { const f = (it.req || []).find((r) => r && r[0] === 39); return f && f[1] >= 0 ? 'HK Môn Phái' : 'HK bộ chung'; }
    if (/^Đồ phổ/i.test(it.n || '')) return 'Đồ phổ';
    if (hasInnate(it)) return 'Đồ vốn có';
    if (/Vô Danh/i.test(it.n || '')) return 'Vô Danh';
    return null;
  };
  const rareBagScan = () => {
    let S, st;
    try { S = window.H5.G('S'); st = window.H5.st; } catch (_) { return; }
    if (!S || !S.name || !st || !(st.items instanceof Map)) return;
    const ids = [...st.items.keys()].map(String);
    if (!bagKnownIds || bagKnownChar !== S.name) { bagKnownIds = new Set(ids); bagKnownChar = S.name; return; }
    const fresh = ids.filter((id) => !bagKnownIds.has(id));
    if (!fresh.length) return;
    for (const id of fresh) bagKnownIds.add(id);
    const mine = [...(S.inv || []), ...Object.values(S.eq || {})].filter(Boolean);
    const now = Date.now();
    for (const id of fresh) {
      const it = mine.find((x) => String(x.h5) === id);
      const label = it && rareLabel(it);
      if (!label) continue;
      const innate = hasInnate(it) ? ` (vốn có +${(it.mag || []).filter((x) => window.JX.attr[x.a] === 'allskill_v').map((x) => (x.p || [])[0]).join('+')})` : '';
      const picked = recentPickup.get(it.n);
      const ev = lastEvent && now - lastEvent.at < 10000 && !(picked && now - picked < 5000) ? lastEvent : null;
      const src = ev ? ev.text : `nhặt ở ${logPlace()}`;
      // Dòng "Nhặt được <tên>" của chính món này (còn trong hàng chờ) → thay bằng dòng 💎 có loại và nguồn
      const q = gameLogQ.findIndex((x) => x.text === `Nhặt được ${it.n}`);
      if (q >= 0) gameLogQ.splice(q, 1);
      gameLogQ.push({ at: now, text: `💎 ${label}: ${it.n}${innate} ← ${src}`, place: ev ? ev.place : logPlace() });
    }
    for (const [k, t] of recentPickup) if (now - t > 30000) recentPickup.delete(k);
  };
  setInterval(() => {
    hookGameLog(); // R.logs có thể được tạo lại (vào game, đổi nhân vật) → móc lại
    try { rareBagScan(); } catch (_) {}
    if (gameLogQ.length) post({ t: 'gameLog', lines: gameLogQ.splice(0, 120) });
  }, 1500);
  // Thông báo nhỏ của game (#toast): báo về app những câu cần biết, ví dụ "Cần đạt cấp 120 để vào Phong Lăng độ"
  // (08/11 22:00: boss Hoàng Kim của một nhân vật ở bản đồ cấp 120, nhân vật cấp 118 – không vào được)
  let toastSeen = '';
  setInterval(() => {
    const el = $('#toast');
    const text = el && el.classList.contains('on') ? el.textContent.trim() : '';
    if (text && text !== toastSeen && /Cần đạt cấp \d+/.test(text)) post({ t: 'gameToast', text: text.slice(0, 160) });
    toastSeen = text;
  }, 400);
  const goldBossNow = () => {
    try { const S = window.H5.G('S'); const g = S && S.gb; return g && g.key ? `${g.key}|${g.killed ? 1 : 0}|${Math.round(100 * (+g.hpf || 0))}` : ''; } catch (_) { return ''; }
  };
  // Boss truy sát đã xuất hiện: bản H5 tạo boss phía trình duyệt với fbK (khoá lượt đánh trên server); bản cũ: sat / ve
  const huntBossHere = () => {
    try { const R = window.H5.G('R'); return (R.enemies || []).some((e) => e && !e.dead && (e.fbK || e.sat || e.ve)); } catch (_) { return false; }
  };
  const assassinActive = () => !!huntNow() || /Sát Thủ/.test((($('#u20trk') || {}).textContent) || '');
  // Đang ở tháp / phó bản (game: "Rời phó bản / tháp trước" khi gọi Sát Thủ)
  const inTowerOrDungeon = () => { try { const R = window.H5.G('R'); return !!(R && (R.tower || R.dg)); } catch (_) { return /Tháp|Phó bản/.test(txt('#u20zone')); } };

  // Nhiệm vụ đang theo dấu ở góc màn hình: bỏ dòng tiêu đề "Nhiệm vụ Theo Dấu ▾", nối các dòng bằng " · "
  const trackerText = () => {
    const el = $('#u20trk');
    if (!el || !isVisible(el)) return '';
    return (el.innerText || '').split('\n').map((l) => l.trim()).filter((l) => l && !/Theo Dấu/i.test(l) && !/^[▾▴▸◂]$/.test(l)).join(' · ').slice(0, 160);
  };

  const readBossHk = (tab) => {
    const title = [...tab.querySelectorAll('.u2qd b, b')].find((b) => /Boss Hoàng Kim/.test(b.textContent));
    const card = title && title.closest('.card');
    if (!card) return '';
    const line = [...card.querySelectorAll(':scope > small')].find((e) => !e.classList.contains('dim'));
    return line ? line.textContent.replace(/\s+/g, ' ').trim().slice(0, 160) : '';
  };
  // Đi thuyền: "Chuyến còn hôm nay 2/3" → "2/3"
  const tabText = (tab) => (tab.querySelector('.u2qd') || tab).innerText || '';
  const readBoat = (tab) => VLParse.boat(tabText(tab));
  // Boss tuần: "Lượt còn 3/3" (3 lượt mỗi tuần) + nút "Nhận rương tuần" sáng → "3/3" hoặc "0/3 claim"
  const readWeekBoss = (tab) => {
    const n = VLParse.weekBoss(tabText(tab));
    if (!n) return '';
    const claim = $('#wbClaim');
    return n + (claim && !claim.disabled ? ' claim' : '');
  };
  // Mở bảng Nhiệm vụ › tab `qt`, bấm nút `sel` (không bấm khi nút mờ), đóng bảng. Trả lời ngắn cho app.
  const pressQuestButton = async (qt, sel) => {
    if (flowBusy || anyWindowOpen()) return 'busy';
    if (inBossHk()) return 'boss-hk';
    const btn = $('#u20menu > button[data-u="quest"]'), tab = $('#t-quest');
    if (!btn || !tab) return 'not-ready';
    btn.click();
    let label = '';
    try {
      const t = tab.querySelector(`[data-qt="${qt}"]`);
      if (!t) return 'no-tab';
      t.click();
      await sleep(250);
      const b = $(sel);
      if (!b) return 'no-button';
      label = b.textContent.trim().slice(0, 40);
      if (b.disabled) return 'disabled: ' + label;
      b.click();
      await sleep(800);
      if (qt === 'boat') quests.boat = readBoat(tab) || quests.boat;
      if (qt === 'wb') quests.wb = readWeekBoss(tab) || quests.wb;
    } finally {
      closeQuestWindows();
    }
    // Đã bấm được nút: từ đây có trục trặc gì cũng vẫn trả "ok" (game đã nhận lệnh)
    let extra = '';
    try {
      await sleep(400);
      const toast = $('#toast');
      if (isVisible(toast)) extra += ' | ' + toast.textContent.trim().slice(0, 80);
      // Game có hỏi lại thì ghi chữ các nút vào log (không tự bấm hộp hỏi)
      if (isVisible($('#askBox'))) extra += ' ask: ' + $('#askBox').innerText.replace(/\s+/g, ' ').slice(0, 120);
    } catch (err) { extra += ' (' + err.message + ')'; }
    return 'ok: ' + label + extra;
  };
  // 👑 Boss Hoàng Kim trong đợt (app gọi): bảng Nhiệm vụ › tab Boss (data-qt="wb") › thẻ Boss Hoàng Kim.
  // Boss còn sống: "<tên> cấp N tại <bản đồ> · còn M phút · Boss còn X% máu" + nút #gbGo "Đến ngay";
  // đánh xong: "Đã hạ Boss đợt này. Đợt sau: …" (chỉ hiện tới hết đợt). arg.go → bấm "Đến ngay" nếu có.
  const bossHkFlow = async (arg) => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    const btn = $('#u20menu > button[data-u="quest"]'), tab = $('#t-quest');
    if (!btn || !tab) return { r: 'not-ready' };
    flowBusy = true;
    const res = { r: 'ok', done: false, alive: false, went: false, info: '' };
    try {
      btn.click();
      const t = await waitFor(() => tab.querySelector('[data-qt="wb"]'), 2000);
      if (!t) return { ...res, r: 'no-tab' };
      t.click();
      const card = await waitFor(() => {
        const b = [...tab.querySelectorAll('b')].find((x) => /Boss Hoàng Kim/.test(x.textContent));
        return b && b.closest('.card');
      }, 2000);
      if (!card) { reportUnreadable('boss-hk', tabText(tab)); return { ...res, r: 'no-card' }; }
      const text = card.innerText.replace(/\s+/g, ' ');
      res.done = /Đã hạ Boss đợt này/.test(text);
      const go = $('#gbGo');
      res.alive = !!go && isVisible(go);
      const m = text.match(/(\S[^·|]*? cấp \d+ tại [^·]+·[^·]+(?:· Boss còn \d+% máu)?)/);
      res.info = (m ? m[1] : text.split('⚔')[0]).trim().slice(0, 140);
      if (arg.go && res.alive && !go.disabled) {
        go.click();
        res.went = true;
        await sleep(800);
      }
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      closeQuestWindows();
      flowBusy = false;
    }
    return res;
  };

  // 💰 Bán đồ thừa (app gọi khi Hành trang sắp đầy): nút "💰 Bán thừa N" (#bSellAll) của game bán các món không khớp
  // bộ lọc ⚙ Nhặt đồ và không được bảo vệ (khoá, đồ Dã Tẩu…). Game hỏi lại "Bán N món đồ thừa (không được giữ,
  // không khóa) lấy X lượng…?" → chỉ đồng ý đúng câu này. Đồ Hoàng Kim nằm ở hàng chờ khi túi đầy sẽ tự vào lại.
  const sellJunkFlow = async () => {
    if (flowBusy || anyWindowOpen()) return { r: 'busy' };
    if (inBossHk()) return { r: 'boss-hk' };
    flowBusy = true;
    const stop = watchMsgs();
    const res = { r: 'ok', sold: 0, gold: '', before: '', after: '' };
    try {
      if (!(await openBag())) return { ...res, r: 'no-bag' };
      const tab = $('#t-inv');
      res.before = readBag(tab);
      const b = $('#bSellAll');
      const n = b ? +(((b.querySelector('small') || {}).textContent || '').replace(/\D/g, '')) || 0 : 0;
      if (!b || b.disabled || !n) return { ...res, r: 'none' };
      b.click();
      const yes = await waitFor(() => {
        const box = $('#askBox');
        if (!box || !isVisible(box)) return null;
        if (!/^Bán \d+ món đồ thừa/.test(box.innerText.trim())) return 'bad';
        return [...box.querySelectorAll('button')].find((x) => x.dataset.a === '1' || /^Bán$/.test(x.textContent.trim())) || 'bad';
      }, 2500);
      if (!yes || yes === 'bad') {
        const box = $('#askBox');
        if (box && isVisible(box)) {
          post({ t: 'log', msg: 'sell junk: unexpected dialog ' + box.innerText.replace(/\s+/g, ' ').slice(0, 120) });
          [...box.querySelectorAll('button')].find((x) => /^Hủy$/.test(x.textContent.trim()))?.click();
        }
        return { ...res, r: 'no-confirm' };
      }
      yes.click();
      await waitFor(() => readBag(tab) !== res.before, 3000);
      res.after = readBag(tab);
      res.sold = n;
      const msg = horseMsgs.find((t) => /^Bán \d+ món \(\+/.test(t));
      const g = msg && msg.match(/\(\+([^)]+?) lượng/);
      res.gold = g ? g[1] : '';
    } catch (err) {
      res.r = 'error: ' + err.message;
    } finally {
      stop();
      try { closeBag(); } catch (_) {}
      flowBusy = false;
    }
    return res;
  };

  // "Lệnh Bài Sát Thủ | 10 | Đã hạ | 60 (cao nhất cấp 90)" → "10/60"
  const readAssassin = (tab) => VLParse.assassin(tabText(tab));
  // Phó bản: "Hết lượt" tính 0 (xem VLParse.dungeons)
  const readDungeons = (tab) => (tab.querySelector('.u2qd') ? VLParse.dungeons(tabText(tab)) : '');

  // Có bảng mà không đọc được số (game đổi chữ / giao diện): báo app một lần mỗi lần tải trang, kèm một đoạn chữ để sửa
  const unreadable = new Set();
  const reportUnreadable = (what, text) => {
    if (unreadable.has(what)) return;
    unreadable.add(what);
    post({ t: 'unreadable', what, sample: String(text || '').replace(/\s+/g, ' ').trim().slice(0, 300) });
  };
  // Đọc một thẻ của bảng Nhiệm vụ; không ra số thì báo
  const readOrReport = (what, tab, read) => {
    const v = read(tab);
    if (!v) reportUnreadable(what, tabText(tab));
    return v;
  };

  const refreshQuests = () => {
    const panel = $('#panel'), tab = $('#t-quest'), btn = $('#u20menu > button[data-u="quest"]');
    if (!panel || !tab || !btn) return;

    if (isVisible($('#modal')) || isOpen($('#u2bagw')) || isOpen($('#u2chw'))) return;
    if (isVisible(panel)) {
      // Người chơi đang mở bảng: chỉ đọc nếu đang xem đúng bảng Nhiệm vụ
      if (isVisible(tab)) {
        mergeQuest(readQuestTab(tab));
        const sel = tab.querySelector('[data-qt].on');
        if (sel && sel.dataset.qt === 'dg') quests.pb = readDungeons(tab);
      }
      return;
    }

    btn.click(); // mở
    try {
      const previous = tab.querySelector('[data-qt].on');
      const dtBtn = tab.querySelector('[data-qt="dt"]'), dgBtn = tab.querySelector('[data-qt="dg"]');
      if (dtBtn) {
        dtBtn.click();
        const dt = readQuestTab(tab);
        if (!dt.dt) reportUnreadable('daTau', tabText(tab));
        mergeQuest(dt);
      }
      // Phó bản không đọc được → coi là chưa biết ('', app bỏ qua bước Phó Bản), KHÔNG giữ số cũ
      // (giữ số cũ "còn 1 lượt" từng làm acc kẹt ở Phó Bản hàng giờ)
      if (dgBtn) { dgBtn.click(); quests.pb = readOrReport('dungeons', tab, readDungeons); }
      // Boss Hoàng Kim: dòng trạng thái ngay dưới tiêu đề, ví dụ "Đã hạ Boss đợt này. Đợt sau: 22:00".
      // Lịch theo giờ Việt Nam nên đọc chữ của game, không dựa vào đồng hồ máy. Sát Thủ: số lệnh bài đang có.
      const wbBtn = tab.querySelector('[data-qt="wb"]');
      if (wbBtn) { wbBtn.click(); quests.hk = readBossHk(tab); quests.wb = readOrReport('weekBoss', tab, readWeekBoss); }
      const boatBtn = tab.querySelector('[data-qt="boat"]');
      if (boatBtn) { boatBtn.click(); quests.boat = readOrReport('boat', tab, readBoat); }
      const stBtn = tab.querySelector('[data-qt="st"]');
      if (stBtn) { stBtn.click(); const st = readOrReport('assassin', tab, readAssassin); if (st) quests.st = st; }
      // Trả bảng về mục người chơi đang chọn lần trước
      const back = previous && previous.dataset.qt ? tab.querySelector(`[data-qt="${previous.dataset.qt}"]`) : dtBtn;
      if (back) back.click();
    } finally {
      // Đóng bằng nút ✕ của bảng (bấm lại nút mở không phải bảng nào cũng đóng)
      const close = $('#u20wx');
      if (isVisible(panel)) (close && isVisible(close) ? close : btn).click();
    }
  };

  // ---------- Đồ phổ ----------
  // Hai nguồn: dòng "Hệ thống" trong chat ("Chúc mừng <tên> nhận được Đồ phổ … khi vượt Tháp!"
  // – thông báo chung cả server, app lọc theo tên nhân vật của bạn) và dòng nhặt đồ của chính
  // nhân vật (.gdl). Lịch sử chat tải lại khi mở trang bị bỏ qua nhờ giờ ở đầu dòng.
  const LOOT_RE = /Đồ\s*phổ/i;
  const seenLoot = new Set();
  const isRecent = (text) => {
    const m = text.match(/^(\d{1,2}):(\d{2})/);
    if (!m) return true;
    const now = new Date();
    let diff = now.getHours() * 60 + now.getMinutes() - (+m[1] * 60 + +m[2]);
    if (diff < -720) diff += 1440; // qua nửa đêm
    return diff >= -2 && diff <= 5;
  };
  // Phần thưởng tháp ở kênh Hệ thống: "Tháp tầng 7190: 3 Phúc Duyên (chờ đóng bình), Lục Thủy Tinh".
  // Gửi app mọi dòng (app chỉ ghi mốc 100 tầng và dòng có đồ Hoàng Kim). Lịch sử cũ khi mở trang bỏ qua nhờ giờ đầu dòng.
  const TOWER_REWARD_RE = /Tháp\s*tầng\s*(\d+)\s*:\s*(.+)$/i;
  const seenReward = new Set();
  // Nhớ dòng đã xử lý; đầy thì bỏ dòng cũ nhất (không xoá sạch – dòng còn trên màn hình sẽ bị báo lại)
  const remember = (set, text, max = 400) => {
    set.add(text);
    if (set.size > max) set.delete(set.values().next().value);
  };
  const EVENT_BOAT_RE = /Qua\s*sông|Thủy\s*Tặc|Phong\s*Lăng\s*Độ/i, EVENT_WB_RE = /Boss\s*tuần|Rương\s*tuần|Khiêu\s*chiến/i;
  // seedOnly: lần đọc đầu sau khi vào game chỉ ghi nhớ (lịch sử cũ), không báo
  const checkTowerReward = (text, seedOnly = false) => {
    const m = text.match(TOWER_REWARD_RE);
    // Chỉ dòng của chính acc: "Nhặt được …". Bỏ thông báo chung của server ("📢 Hệ thống: Chúc mừng X nhận được …")
    if (/Chúc mừng|📢|Hệ thống\s*:/.test(text)) return;
    const golden = !m && /Hoàng Kim/.test(text) && /^(\d{1,2}:\d{2}\s*)?Nhặt được/i.test(text);
    // Phần thưởng sau chuyến thuyền / lượt Boss tuần (app ghi vào nhật ký, coi là đã xong lượt đó)
    const event = !m && !golden ? (EVENT_BOAT_RE.test(text) ? 'boat' : EVENT_WB_RE.test(text) ? 'weekBoss' : '') : '';
    if ((!m && !golden && !event) || seenReward.has(text) || !isRecent(text)) return;
    remember(seenReward, text);
    if (seedOnly) return;
    if (event) post({ t: 'eventReward', kind: event, text: text.slice(0, 200) });
    else if (m) post({ t: 'towerReward', floor: +m[1], reward: m[2].trim().slice(0, 200) });
    else post({ t: 'towerReward', floor: 0, reward: text.slice(0, 200) }); // đồ Hoàng Kim nhặt được (không ghi tầng)
  };

  // Kênh Hệ thống của khung chat: game chỉ vẽ dòng của kênh đang chọn (#u20lines), và kênh này bị ngập dòng
  // "Nhận được … điểm kinh nghiệm" (vài dòng mỗi giây, game chỉ giữ vài chục dòng) → đọc định kỳ sẽ trôi mất dòng thưởng.
  // Vì vậy: giữ thẻ chat ở "Hệ thống" (app tắt khi bạn đang xem / dùng khung đó) và nghe từng dòng mới ngay khi game vẽ.
  // Không bấm gì trong game nên chạy cả trong đợt Boss Hoàng Kim.
  let keepSys = true, sysTabAt = 0;
  const sysActive = () => {
    const l = $('#u20lines');
    return !!l && (l.dataset.lt === 'sys' || !!document.querySelector('[data-lt="sys"].on'));
  };
  // Luôn mở thẻ "Hệ thống" và luôn hiện khung dòng (nút 📜 #u20lt thu gọn / mở; thu gọn thì #u20lines ẩn)
  let sysShowAt = 0;
  const ensureSysTab = () => {
    if (!keepSys || document.activeElement === $('#u20in')) return;
    const lines = $('#u20lines');
    if (lines && getComputedStyle(lines).display === 'none' && Date.now() - sysShowAt > 15000) {
      const t = $('#u20lt');
      if (t) { sysShowAt = Date.now(); t.click(); }
    }
    if (sysActive() || Date.now() - sysTabAt < 5000) return;
    const tab = $('[data-lt="sys"]:not(#u20lines)');
    if (!tab) return;
    sysTabAt = Date.now();
    tab.click();
  };
  // Dòng rác của kênh Hệ thống (kinh nghiệm, lượng, KNB khóa rơi, tự mua thuốc…) và dòng đáng ghi nhưng chưa biết mẫu
  const SYS_NOISE_RE = /^(Nhận được [\d.,]+ điểm kinh nghiệm|Nhặt được [\d.,]+ lượng|💰 \+\d+ KNB khóa|Tự mua )/;
  const SYS_NOTE_RE = /thưởng|Boss|Rương|Hoàng Kim|Mốc|Tinh Hồng|Khoáng Thạch|Thủy Tinh|Sát Thủ/i;
  const seenSys = new Set();
  let sysPrimed = false; // đã qua lượt vẽ đầu (lịch sử) của thẻ Hệ thống chưa
  const handleSysLine = (text, seedOnly) => {
    if (!text || SYS_NOISE_RE.test(text)) return;
    checkTowerReward(text, seedOnly);
    // Dòng có từ khoá phần thưởng mà chưa có mẫu riêng (ví dụ thưởng Boss Hoàng Kim): gửi app ghi lại để học mẫu
    if (TOWER_REWARD_RE.test(text) || /Chúc mừng|📢|Hệ thống\s*:/.test(text) || !SYS_NOTE_RE.test(text) || seenSys.has(text)) return;
    remember(seenSys, text);
    if (!seedOnly) post({ t: 'sysNote', text: text.slice(0, 200), boss: inBossHk() });
  };
  const checkLoot = (node) => {
    if (!(node instanceof Element)) return;
    const lines = node.matches('.cl, .gdl') ? [node] : node.querySelectorAll('.cl, .gdl');
    for (const line of lines) {
      const text = (line.textContent || '').trim().replace(/\s+/g, ' ');
      checkTowerReward(text);
      if (!LOOT_RE.test(text) || seenLoot.has(text)) continue;
      const pickup = line.classList.contains('gdl');
      if (!pickup && (!/Hệ thống/i.test(text) || !isRecent(text))) continue;
      if (seenLoot.size > 500) seenLoot.clear();
      seenLoot.add(text);
      post({ t: 'loot', src: pickup ? 'pickup' : 'system', text: text.slice(0, 240) });
    }
  };
  // Script chạy trước khi trang có HTML → chờ có phần tử gốc rồi mới theo dõi.
  // Lỗi ở phần này không được làm hỏng các phần khác của script (báo trạng thái, tự đăng nhập…).
  const watchLoot = () => {
    const root = document.documentElement;
    if (!root) { setTimeout(watchLoot, 50); return; }
    try {
      new MutationObserver((mutations) => {
        for (const m of mutations) {
          m.addedNodes.forEach((n) => { try { checkLoot(n); } catch (_) {} });
          // Kênh Hệ thống: game xoá rồi vẽ lại cả khung (40 dòng) mỗi lần có dòng mới → chỉ xử lý dòng chưa từng thấy.
          // Lượt vẽ đầu tiên sau khi tải trang / vừa chuyển sang thẻ Hệ thống là lịch sử cũ: chỉ ghi nhớ, không báo.
          const host = m.target instanceof Element ? m.target.closest('#u20lines') : null;
          if (!host || !m.addedNodes.length) continue;
          if (!sysActive()) { sysPrimed = false; continue; }
          const seed = !sysPrimed;
          sysPrimed = true;
          m.addedNodes.forEach((n) => {
            try { handleSysLine((n.textContent || '').trim().replace(/\s+/g, ' '), seed); } catch (_) {}
          });
        }
      }).observe(root, { childList: true, subtree: true });
    } catch (err) {
      post({ t: 'log', msg: 'watch loot failed: ' + err.message });
    }
  };
  watchLoot();

  // ---------- Báo trạng thái ----------
  // Gửi khi có thay đổi, và ít nhất 10 giây một lần để app biết trang còn sống
  let last = '', lastAt = 0;
  // Game chống ghi đè: "⚠ Nhân vật vừa được lưu ở thiết bị / tab khác … máy này đã dừng lưu" (thường bật lên đúng lúc
  // Boss Hoàng Kim ra). Lúc đó game đứng yên tới khi bấm "Tải bản mới" → tự bấm (chờ 5 giây cho chắc), tối đa 2 phút/lần.
  let tabLockSeen = 0, tabLockAt = 0;
  const handleTabLock = () => {
    const go = $('#tabLockBox #tabGo');
    if (!go || !isVisible(go)) { tabLockSeen = 0; return; }
    if (!tabLockSeen) tabLockSeen = Date.now();
    if (Date.now() - tabLockSeen < 5000 || Date.now() - tabLockAt < 120000) return;
    tabLockAt = Date.now();
    post({ t: 'tabLock' });
    go.click();
  };

  // "Tài khoản đang ở nơi khác": game đẩy ra, hiện 2 nút "Chơi ở đây" / "Đăng xuất". Trang chỉ báo (status.away),
  // app quyết định lúc nào bấm "Chơi ở đây" (chờ một lúc, chống giành phiên qua lại). Không bao giờ bấm "Đăng xuất".
  const PLAY_HERE_RE = /Chơi\s*ở\s*đây/i;
  const playHereButton = () => [...document.querySelectorAll('button, a, [role="button"]')]
    .find((b) => isVisible(b) && !b.disabled && PLAY_HERE_RE.test(b.textContent || '')) || null;

  // Hộp thông báo lạ của game: chỉ ghi chữ và các nút vào log (mỗi nội dung một lần) để biết cách xử lý về sau.
  // Bỏ qua bảng/cửa sổ thường dùng (#panel, #modal) và hộp đã biết (#maint, #tabLockBox).
  const seenDialogs = new Set();
  let dialogLogs = 0; // tối đa 30 dòng mỗi lần tải trang
  // Hộp nổi: position fixed (hoặc nằm trong một lớp fixed), không phải khung chat / thanh cố định của game
  const floating = (el) => el.id === 'askBox' || el.getAttribute('role') === 'dialog'
    || [el, el.parentElement].some((x) => x && getComputedStyle(x).position === 'fixed');
  const DIALOG_SEL = '#askBox, [role="dialog"], [id*="box" i], [id*="dlg" i], [id*="popup" i], [id*="overlay" i], [class*="dialog" i], [class*="popup" i]';
  const logDialogs = () => {
    if (dialogLogs >= 30) return;
    const found = [...document.querySelectorAll(DIALOG_SEL)].filter(floating);
    const ph = playHereButton();
    if (ph) found.push(ph.closest('div:not(body)') || ph.parentElement);
    for (const el of found) {
      if (!el || ['panel', 'modal', 'maint', 'tabLockBox', 'ptBox'].includes(el.id) || el.closest('#panel, #modal')) continue;
      if (!isVisible(el) || !el.querySelector('button, a, [role="button"]')) continue;
      const full = (el.innerText || '').replace(/\s+/g, ' ').trim();
      if (full.length > 600) continue; // khối lớn (cả màn hình game), không phải hộp thông báo
      const text = full.slice(0, 200);
      const key = (el.id || el.className || el.tagName) + '|' + text;
      if (!text || seenDialogs.has(key)) continue;
      seenDialogs.add(key);
      dialogLogs++;
      const buttons = [...el.querySelectorAll('button, a, [role="button"]')].filter(isVisible)
        .map((b) => (b.textContent || '').trim().slice(0, 30) + (b.id ? '#' + b.id : '')).join(' | ');
      post({ t: 'log', msg: `dialog ${el.id ? '#' + el.id : el.tagName + '.' + String(el.className).slice(0, 40)}: "${text}" buttons: ${buttons}` });
    }
  };

  // Chỉ để kiểm tra sau khi cập nhật (đọc qua DevTools): trang đang hiểu số liệu thế nào. Không thay đổi gì.
  window.__vlDebug = () => ({
    version: 2, quests: { ...quests }, bag, doPho, knb: knbNow(), maintenance: inMaintenance(), keepSys,
    sysActive: sysActive(), away: !!playHereButton(), flowBusy, unreadable: [...unreadable],
  });

  const report = () => {
    try { handleTabLock(); } catch (err) { post({ t: 'log', msg: 'tab lock failed: ' + err.message }); }
    try { logDialogs(); } catch (err) { post({ t: 'log', msg: 'dialog scan failed: ' + err.message }); }
    const screen = screenOf();
    if (screen !== 'game') gameSince = 0;
    else if (!gameSince) gameSince = Date.now();
    // Chờ game tải xong hẳn rồi mới đụng tới bảng Nhiệm vụ
    if (screen === 'game' && !flowBusy && !inBossHk() && Date.now() - gameSince > 15000 && Date.now() - questsAt > QUEST_REFRESH_MS) {
      questsAt = Date.now();
      try { refreshQuests(); } catch (err) { post({ t: 'log', msg: 'read quests failed: ' + err.message }); }
    }
    if (screen === 'game' && !flowBusy && !inBossHk() && Date.now() - gameSince > 20000 && Date.now() - shopAt > SHOP_REFRESH_MS) {
      shopAt = Date.now();
      try { refreshShop(); } catch (err) { post({ t: 'log', msg: 'read shop failed: ' + err.message }); }
    }
    if (screen === 'game' && !flowBusy && Date.now() - gameSince > 10000) {
      try { ensureSysTab(); } catch (err) { post({ t: 'log', msg: 'system tab failed: ' + err.message }); }
    }
    if (screen === 'game' && !flowBusy && !inBossHk() && Date.now() - gameSince > 25000 && Date.now() - bagAt > BAG_REFRESH_MS) {
      bagAt = Date.now();
      try { refreshBag(); } catch (err) { post({ t: 'log', msg: 'read bag failed: ' + err.message }); }
    }
    const on = $('#mswMenu button.on');
    const btnMode = on ? on.dataset.msw || '' : '';
    const realMode = screen === 'game' ? reportedMode(btnMode) : btnMode;
    const s = {
      t: 'status',
      screen,
      // Tên nhân vật: trong game đọc thẳng S.name (màn chọn nhân vật đổi giao diện từ bản cập nhật 11/2026, đọc chữ
      // "Nhân vật" ở đó không còn ra tên – acc mới thêm không có tên, 🐴 Ngựa / gửi KNB / duyệt bang không chạy)
      name: screen === 'charselect' ? charSelectName() : screen === 'game' ? gameCharName() : '',
      loginMsg: screen === 'login' ? loginMessage() : '',
      lv: txt('#lvS'),
      zone: txt('#u20zn'), // ví dụ "Thông Thiên Tháp · tầng 632"
      // Môn phái: game không còn ghi bằng chữ, chỉ còn biểu tượng phái ở ô cấp ("img/pl/wudang.png")
      sect: ((($('.lvbox') || {}).getAttribute?.('style') || '').match(/img\/pl\/([a-z0-9_-]+)\./i) || [])[1] || '',
      dt: screen === 'game' ? quests.dt : '',
      pb: screen === 'game' ? quests.pb : '',
      ldtHave: screen === 'game' ? quests.ldtHave : '',     // lệnh bài đang có
      ldtUsed: screen === 'game' ? quests.ldtUsed : '',     // đã dùng hôm nay, ví dụ "35/160"
      ldtBuy: screen === 'game' ? quests.ldtBuy || '' : '', // đã mua hôm nay, ví dụ "55/100"
      dtMax: screen === 'game' ? quests.dtMax : '',         // nhiệm vụ Dã Tẩu tối đa mỗi ngày (200)
      hk: screen === 'game' ? quests.hk || '' : '',         // trạng thái Boss Hoàng Kim
      st: screen === 'game' ? quests.st || '' : '',         // Lệnh Bài Sát Thủ đang có / đã hạ
      boat: screen === 'game' ? quests.boat || '' : '',     // chuyến thuyền còn hôm nay "2/3"
      wb: screen === 'game' ? quests.wb || '' : '',         // lượt Boss tuần còn "3/3" (+ " claim" khi nhận được rương tuần)
      bt: inMaintenance() ? 1 : 0,                          // máy chủ game đang bảo trì
      away: playHereButton() ? 1 : 0,                       // "Tài khoản đang ở nơi khác" (nút Chơi ở đây đang hiện)
      bag: screen === 'game' ? bag : '',                     // hòm đồ "18/120"
      // Đồ phổ đang giữ, mỗi dòng một món; chưa quét xong lần đầu thì không gửi
      dp: screen === 'game' && doPho ? doPho.join('\n') : undefined,
      xp: screen === 'game' ? txt('#xpTxt') : '',            // kinh nghiệm cấp hiện tại "92.5%"
      trk: screen === 'game' ? trackerText() : '',
      // Truy sát đang mở: "<tên>|<% máu còn>" ("Sát Thủ · Bảo Triệt Sơn|79")
      // + "|1" khi boss của truy sát đang ở bên cạnh (game đã cho xuất hiện)
      hunt: screen === 'game' ? ((h) => (h ? `${h.n}|${Math.round(h.hpf * 100)}|${huntBossHere() ? 1 : 0}` : ''))(huntNow()) : '',
      // Boss Hoàng Kim đợt này của riêng acc (game S.gb, mỗi nhân vật một con boss): "<đợt>|<1 = đã hạ>|<% máu boss>"
      gbw: screen === 'game' ? goldBossNow() : '',
      innate: screen === 'game' ? innateUnlocked().length + innateEquippedUnlocked().length : 0, // đồ có kỹ năng vốn có chưa khoá
      gold: txt('#gold'),
      knb: txt('#knbTop'),
      // Chế độ: theo nút đang sáng, nhưng nút lệch với trạng thái game quá 5 giây thì theo game (xem reportedMode)
      mode: realMode,
      modeText: realMode !== btnMode ? `${realMode} (nút: ${btnMode})` : on ? on.textContent.trim() : '',
      inTower: screen === 'game' ? ((gm) => (gm ? (gm.tower ? 1 : 0) : -1))(gameModeNow()) : -1,
    };
    const json = JSON.stringify(s);
    if (json !== last || Date.now() - lastAt > 10000) {
      last = json;
      lastAt = Date.now();
      post(s);
    }
  };
  setInterval(report, 1500);

  post({ t: 'hello' });
})();
