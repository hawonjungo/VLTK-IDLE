// Đọc số liệu từ CHỮ của game (không đụng DOM). Nạp vào trang trước agent.js (PageScripts.Agent),
// và được test bằng chữ chép từ game thật (tests/agent). Mọi hàm: chữ không đúng mẫu → null / '' ("không đọc được"),
// không bao giờ đoán là 0 – agent báo app "không đọc được" để app bỏ qua bước đó thay vì làm sai.
// Lưu ý cách game viết số: "17.2k" / "1.5m" (dấu chấm là phần thập phân) nhưng "9.435" (dấu chấm ngăn cách nghìn).
var VLParse = (() => {
  const clean = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim();

  // "17.2k" → 17200, "1.5m" → 1500000, "9.435" → 9435, "1341" → 1341; sai mẫu → null
  const num = (s) => {
    const m = clean(s).match(/^(\d+(?:[.,]\d+)*)\s*([kKmM])?$/);
    if (!m) return null;
    if (m[2]) return Math.round(parseFloat(m[1].replace(',', '.')) * (/k/i.test(m[2]) ? 1e3 : 1e6));
    return +m[1].replace(/[.,]/g, '');
  };

  // Ô KNB trên cùng: "17.2k 🔒1113", "1341 🔒427", "2.1k 🔒1.1k" → { knb, khoa }
  const knb = (text) => {
    const m = clean(text).match(/^([\d.,]+\s*[kKmM]?)\s*🔒\s*([\d.,]+\s*[kKmM]?)/);
    if (!m) return null;
    const a = num(m[1]), b = num(m[2]);
    return a == null || b == null ? null : { knb: a, khoa: b };
  };

  const frac = (s) => clean(s).replace(/\s*\/\s*/, '/');

  // Bảng Nhiệm vụ › Dã Tẩu: "Hôm nay 162/162 (+122 lệnh bài)", "Lệnh Bài Dã Tẩu: có 0 · … đã dùng 122/160 (tối đa 200 nhiệm vụ / ngày)"
  const daTau = (text) => {
    const t = clean(text);
    // Viết hoa "Hôm nay" (thẻ Đi thuyền có "Chuyến còn hôm nay 0/3" viết thường – không được lẫn)
    const dt = t.match(/Hôm\s*nay\s*(\d+\s*\/\s*\d+(?:\s*\(\+?\d+ lệnh bài\))?)/);
    const line = (t.match(/Lệnh Bài Dã Tẩu\s*:[^]*?(?=Dùng Lệnh Bài|$)/i) || [''])[0];
    const have = line.match(/có\s+(\d+)/i), used = line.match(/đã dùng\s+(\d+)\s*\/\s*(\d+)/i), max = line.match(/tối đa\s+(\d+)\s+nhiệm vụ/i);
    return {
      dt: dt ? clean(dt[1]).replace(/\s*\/\s*/, '/') : '',
      ldtHave: have ? have[1] : '',
      ldtUsed: used ? `${used[1]}/${used[2]}` : '',
      dtMax: max ? max[1] : '',
    };
  };

  // Bảng Nhiệm vụ › Phó bản: mỗi phó bản đã mở có "Vào còn/tối đa", hoặc "Hết lượt" khi đi hết (KHÔNG phải "Vào 0/2")
  // → tính 0. Phó bản chưa mở (sau 🔒) không có nút. Cộng cả acc: "0/10"; không thấy nút nào → ''.
  const dungeons = (text) => {
    const t = clean(text);
    const perDay = +((t.match(/(\d+)\s*lượt\s*\/\s*ngày/) || [])[1] || 2);
    let left = 0, max = 0, found = false;
    for (const m of t.matchAll(/Vào\s+(\d+)\s*\/\s*(\d+)|Hết\s*lượt/g)) {
      found = true;
      if (m[1] != null) { left += +m[1]; max += +m[2]; } else max += perDay;
    }
    return found ? `${left}/${max}` : '';
  };

  // ⛵ Đi thuyền: "Chuyến còn hôm nay 2/3" → "2/3"
  const boat = (text) => {
    const m = clean(text).match(/Chuyến còn hôm nay\s*(\d+\s*\/\s*\d+)/i);
    return m ? frac(m[1]) : '';
  };

  // 👹 Boss tuần: "Lượt còn 3/3" → "3/3"
  const weekBoss = (text) => {
    const m = clean(text).match(/Lượt còn\s*(\d+\s*\/\s*\d+)/i);
    return m ? frac(m[1]) : '';
  };

  // 🗡 Sát Thủ: thẻ "Lệnh Bài Sát Thủ | 10 | Đã hạ | 60 (cao nhất cấp 90)" → "10/60"
  const assassin = (text) => {
    const t = clean(text);
    const have = t.match(/Lệnh Bài Sát Thủ\s*(\d+)/i);
    if (!have) return '';
    const killed = t.match(/Đã hạ\s*(\d+)/i);
    return `${have[1]}/${killed ? killed[1] : '?'}`;
  };

  // Kỳ Trân Các › Lệnh Bài Dã Tẩu: "Hôm nay 55/100 · có 21" → { bought, max, have }
  const ldtShop = (text) => {
    const m = clean(text).match(/Hôm nay\s+(\d+)\s*\/\s*(\d+)\s*·\s*có\s+(\d+)/i);
    return m ? { bought: +m[1], max: +m[2], have: +m[3] } : null;
  };

  // Sự kiện 🌹 Tri Ân Nhà Giáo (🎁 Phần thưởng › Tri Ân Nhà Giáo):
  //   "🎀 Giấy Gói: 0" · "Giấy Gói 10 vạn lượng · Lẵng Mây 100 KNB" · "✉ Thiệp Tri Ân: 37" · "💐 Bó Hoa Tri Ân: 170"
  //   "🌹 Hoa Hồng: 2222" · "Bó Hoa 198 (hôm nay 0/40)" (điểm từ Bó Hoa, hôm nay đã tặng / tối đa mỗi ngày) · "hết ngày 20/11"
  // wrapPrice: giá 1 Giấy Gói tính bằng vạn lượng; null nếu giá không ghi bằng lượng (không được mua).
  const teacher = (text) => {
    const t = clean(text);
    const n = (re) => { const m = t.match(re); return m ? +m[1] : null; };
    const today = t.match(/Bó Hoa\s+(\d+)\s*\(hôm nay\s*(\d+)\s*\/\s*(\d+)\)/);
    const r = {
      points: today ? +today[1] : null, today: today ? +today[2] : null, cap: today ? +today[3] : null,
      bouquets: n(/💐\s*Bó Hoa Tri Ân:\s*(\d+)/), cards: n(/✉\s*Thiệp Tri Ân:\s*(\d+)/),
      roses: n(/🌹\s*Hoa Hồng:\s*(\d+)/), wraps: n(/🎀\s*Giấy Gói:\s*(\d+)/),
      wrapPrice: n(/Giấy Gói\s+(\d+)\s*vạn lượng/), endsOn: (t.match(/hết ngày\s*(\d{1,2}\/\d{1,2})/) || [])[1] || '',
      paper: n(/Giấy Hồng Điều:\s*(\d+)/), brush: n(/Bút Lông:\s*(\d+)/), ink: n(/Mực Tàu:\s*(\d+)/),
      daysLeft: null, exchanges: [],
    };
    // "còn 22 ngày 7 giờ" → còn 23 ngày tặng được (tính cả hôm nay)
    const left = t.match(/còn\s+(\d+)\s*ngày(?:\s*(\d+)\s*giờ)?/);
    if (left) r.daysLeft = +left[1] + (left[2] && +left[2] > 0 ? 1 : 0);
    // "Đổi nguyên liệu thừa": "1 Đại Hồng Bao 100 mỗi loại (400 món) · 0/1 hôm nay (cả tài khoản) Đổi" – theo thứ tự
    // trên bảng, trùng thứ tự nút data-ng="doi:0".."doi:4"
    const ex = t.split(/Đổi nguyên liệu thừa/)[1] || '';
    for (const m of ex.matchAll(/1\s+(.+?)\s+(\d+)\s*mỗi loại[^·]*·\s*(\d+)\s*\/\s*(\d+)\s*hôm nay/g))
      r.exchanges.push({ name: m[1], cost: +m[2], done: +m[3], max: +m[4] });
    return [r.today, r.cap, r.bouquets, r.cards, r.roses, r.wraps].some((v) => v == null) ? null : r;
  };

  // Ngân lượng: ô #gold "12.754 vạn" / Kỳ Trân Các "6329 vạn 3023 lượng" → số vạn; chỉ có lượng ("8521 lượng") → 0
  const silver = (text) => {
    const t = clean(text);
    const m = t.match(/([\d.,]+\s*[kKmM]?)\s*vạn/);
    if (m) return num(m[1]);
    return /^[\d.,]+\s*lượng$/.test(t) ? 0 : null;
  };

  // Kỳ Trân Các › Bảng Kim Nguyên Bảo › Đổi KNB, thẻ Kim Nguyên Bảo: "1 vạn lượng = 1 KNB · tối đa 1.000 / ngày …
  // Hôm nay đã đổi 0 · còn đổi được 1.000" → { done, left, price } (price = vạn cho 1 KNB)
  const knbShop = (text) => {
    const t = clean(text);
    const m = t.match(/Hôm nay đã đổi\s+([\d.,]+)\s*·\s*còn đổi được\s+([\d.,]+)/i);
    const p = t.match(/(\d+)\s*vạn lượng\s*=\s*1\s*KNB/i);
    if (!m || !p) return null;
    const done = num(m[1]), left = num(m[2]);
    return done == null || left == null ? null : { done, left, price: +p[1] };
  };

  // Thời gian còn lại của một trạng thái trên thanh #skBuffs: "40h" / "1m" / "22s" → giây; "N/A" (không hết hạn) → Infinity
  const buffLeft = (text) => {
    const t = clean(text);
    if (/^N\/?A$/i.test(t)) return Infinity;
    const m = t.match(/^(\d+)\s*([hms])$/i);
    return m ? +m[1] * ({ h: 3600, m: 60, s: 1 })[m[2].toLowerCase()] : null;
  };

  return { num, knb, daTau, dungeons, boat, weekBoss, assassin, ldtShop, teacher, silver, knbShop, buffLeft };
})();
if (typeof module !== 'undefined') module.exports = VLParse;
