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

  const screenOf = () => {
    if (passwordField()) return 'login';
    const go = $('#csGo');
    if (go && isVisible(go)) return 'charselect';
    if ($('#mswBtn')) return 'game';
    return 'loading';
  };

  // Màn chọn nhân vật có bảng "Nhân vật | <tên>" – lấy ô ngay sau nhãn đó
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
    const submit = [...document.querySelectorAll('button, input[type="submit"], [role="button"]')]
      .find((b) => isVisible(b) && /^đăng nhập$/i.test(textOf(b))) || null;
    return { pw, user, submit };
  };

  // Thông báo đang hiện trên hộp đăng nhập (lỗi mạng, sai mật khẩu…) – mọi dòng chữ trừ nhãn cố định
  const LOGIN_LABELS = /^(Xác nhận tên tài khoản|Tài khoản:|Mật mã:|Hiện mật mã|Nhớ tài khoản|Đăng nhập|Đăng ký|Quên mật mã\?|✕)$/i;
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
    return !!b && /^đăng nhập$/i.test(textOf(b));
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
    mode: (code) => {
      const opt = $(`#mswMenu button[data-msw="${CSS.escape(code)}"]`);
      if (!opt) return 'not-in-game';
      if (opt.classList.contains('on')) return 'already';
      opt.click();
      return 'ok';
    },
    enter: () => {
      const go = $('#csGo');
      if (!go || !isVisible(go)) return $('#mswBtn') ? 'already' : 'not-ready';
      go.click();
      return 'ok';
    },
    login: (arg) => {
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
  const bigRedCount = () => { const m = (($('#bHbOpen') || {}).title || '').match(/Đại Hồng Bao\s+(\d+)/i); return m ? +m[1] : 0; };
  const openBigRed = async () => {
    closeModal();
    await waitFor(() => !isVisible($('#modal')), 2000);
    const inv = $('#u20menu > button[data-u="inv"]'), win = $('#u2bagw');
    if (!inv) return 0;
    if (!isOpen(win)) inv.click();
    let got = 0;
    try {
      const hb = await waitFor(() => { const b = $('#bHbOpen'); return b && isVisible(b) ? b : null; }, 2000);
      if (!hb) return 0;
      hb.click();
      const use = await waitFor(() => /Đại Hồng Bao/.test(($('#mBody h3') || {}).textContent || '') && $('#kiUse'), 2000);
      if (!use) return 0;
      const before = knbNow();
      use.click();
      const after = await waitFor(() => { const k = knbNow(); return k && before && k.knb > before.knb ? k : null; }, 8000);
      got = after ? after.knb - before.knb : 0;
    } finally {
      closeModal();
      const w = $('#u2bagw'), x = $('#u2bwx'); if (x && isOpen(w)) x.click();
    }
    return got;
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
  const assassinActive = () => /Sát Thủ/.test((($('#u20trk') || {}).textContent) || '');

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
  const ensureSysTab = () => {
    if (!keepSys || sysActive() || Date.now() - sysTabAt < 20000 || document.activeElement === $('#u20in')) return;
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
    if (screen === 'game' && !flowBusy && !anyWindowOpen() && Date.now() - gameSince > 10000) {
      try { ensureSysTab(); } catch (err) { post({ t: 'log', msg: 'system tab failed: ' + err.message }); }
    }
    if (screen === 'game' && !flowBusy && !inBossHk() && Date.now() - gameSince > 25000 && Date.now() - bagAt > BAG_REFRESH_MS) {
      bagAt = Date.now();
      try { refreshBag(); } catch (err) { post({ t: 'log', msg: 'read bag failed: ' + err.message }); }
    }
    const on = $('#mswMenu button.on');
    const s = {
      t: 'status',
      screen,
      name: screen === 'charselect' ? charSelectName() : '',
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
      gold: txt('#gold'),
      knb: txt('#knbTop'),
      mode: on ? on.dataset.msw || '' : '',
      modeText: on ? on.textContent.trim() : '',
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
