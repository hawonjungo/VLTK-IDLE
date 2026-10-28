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

  return { num, knb, daTau, dungeons, boat, weekBoss, assassin, ldtShop };
})();
if (typeof module !== 'undefined') module.exports = VLParse;
