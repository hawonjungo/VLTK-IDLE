// Test phần đọc số (src/VLMulti.Core/Page/parse.js) bằng chữ chép từ game thật (fixtures/*.json, mỗi file là các bảng
// của một acc vào một ngày). Game đổi giao diện → chép mẫu mới vào fixtures/ là test chỉ ra ngay chỗ đọc hỏng.
// Chạy: node --test tests/agent/parse.test.mjs (publish*.ps1 và dotnet test cũng chạy, nếu máy có Node)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const P = require('../../src/VLMulti.Core/Page/parse.js');
const dir = new URL('./fixtures/', import.meta.url);
const all = readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => [f, JSON.parse(readFileSync(new URL(f, dir), 'utf8'))]);
// Mẫu các bảng thường ngày của một acc (Nhiệm vụ, Kỳ Trân Các…) / mẫu bảng sự kiện (event-*.json)
const fixtures = all.filter(([f]) => !f.startsWith('event-'));

test('sự kiện Tri Ân Nhà Giáo (mẫu thật)', () => {
  const ev = all.find(([f]) => f.startsWith('event-teacher-'))[1];
  assert.deepEqual(P.teacher(ev.text), {
    points: 198, today: 0, cap: 40, bouquets: 170, cards: 37, roses: 2222, wraps: 0, wrapPrice: 10, endsOn: '20/11',
    paper: 1376, brush: 1420, ink: 1392, daysLeft: 24,
    exchanges: [
      { name: 'Tiên Thảo Lộ', cost: 5, done: 0, max: 20 },
      { name: 'Quế Hoa Tửu', cost: 5, done: 0, max: 24 },
      { name: 'Hồng Bao', cost: 20, done: 0, max: 3 },
      { name: 'Tiên Thảo Lộ Đặc Biệt', cost: 50, done: 0, max: 3 },
      { name: 'Đại Hồng Bao', cost: 100, done: 0, max: 1 },
    ],
  });
  assert.equal(P.teacher(ev.text.replace('còn 23 ngày 10 giờ', 'còn 0 ngày 3 giờ')).daysLeft, 1);
  // Giá Giấy Gói không ghi bằng lượng → không được mua
  assert.equal(P.teacher(ev.text.replace('Giấy Gói 10 vạn lượng', 'Giấy Gói 10 KNB')).wrapPrice, null);
  assert.equal(P.teacher('bảng khác'), null);
});

test('Kỳ Trân Các › Đổi KNB, ngân lượng, thời gian trạng thái (mẫu thật)', () => {
  const fx = all.find(([f]) => f.startsWith('event-knbshop-'))[1];
  // Thẻ Kim Nguyên Bảo đứng trước thẻ Đổi KNB → KNB khóa (cũng có "Hôm nay đã đổi 410") – phải lấy đúng thẻ đầu
  assert.deepEqual(P.knbShop(fx.kbText), { done: 0, left: 1000, price: 1 });
  assert.deepEqual(P.knbShop('Hôm nay đã đổi 1.000 · còn đổi được 0 · 1 vạn lượng = 1 KNB'), { done: 1000, left: 0, price: 1 });
  assert.equal(P.knbShop('Hôm nay đã đổi 410'), null);
  // Bản 1035 (02/11): bỏ "còn đổi được", chỉ còn "Hôm nay đã đổi N" → tính từ "tối đa 1.000 / ngày"
  assert.deepEqual(P.knbShop(fx.kbText1035), { done: 0, left: 1000, price: 1 });
  assert.deepEqual(P.knbShop(fx.kbText1035.replace('đã đổi 0', 'đã đổi 600')), { done: 600, left: 400, price: 1 });
  assert.deepEqual(fx.gold.map(P.silver), [12754, 6341]);
  assert.equal(P.silver('6329 vạn 3023 lượng'), 6329);
  assert.equal(P.silver('8521 lượng'), 0);
  assert.equal(P.silver(''), null);
  assert.deepEqual(fx.buffs.map(P.buffLeft), [Infinity, 36000, 727 * 3600, 151 * 3600, 22, 60]);
  assert.equal(P.buffLeft('?'), null);
});

test('Bang hội: cửa hàng, điểm cống hiến, góp lượng (mẫu thật)', () => {
  const g = all.find(([f]) => f.startsWith('event-guild-'))[1];
  assert.deepEqual(P.guildItem(g.ldt), { price: 20, done: 0, max: 10, weekly: false, locked: false });
  assert.deepEqual(P.guildItem(g.ttsp), { price: 300, done: 5, max: 5, weekly: true, locked: false });
  assert.deepEqual(P.guildItem(g.fdl1), { price: 100, done: 0, max: 10, weekly: false, locked: false });
  assert.deepEqual(P.guildItem(g.ttk), { price: 600, done: 0, max: 0, weekly: false, locked: true });
  assert.equal(P.guildItem('Đổi'), null);
  assert.equal(P.guildPoints(g.shopHead), 3613);
  assert.equal(P.guildPoints(g.gopHead), 3613);
  assert.equal(P.guildPoints('Báo | 🏯 Bang'), null);
  assert.deepEqual(P.guildDonate(g.gop), { done: 0, cap: 20000, can: 21400 });
  assert.deepEqual(P.guildDonate(g.gop.replace('Hôm nay 0', 'Hôm nay 1.500')), { done: 1500, cap: 20000, can: 21400 });
});

test('Mã Trường: bộ sưu tập, dòng ngựa, số chỗ (mẫu thật)', () => {
  const h = all.find(([f]) => f.startsWith('event-horse-'))[1];
  const c = P.horseCollection(h.collection);
  assert.equal(c.have, 40);
  assert.equal(c.total, 62);
  assert.equal(c.thanHave, 1);
  assert.equal(c.thanTotal, 14);
  // 62 loài của game đã gồm 14 Thần Mã (48 ngựa thường + 14)
  assert.equal(c.owned.length + c.missing.length, 62);
  assert.equal(c.owned.length, 40);
  assert.ok(c.owned.includes('hoàng mã'));
  assert.ok(c.missing.includes('sư tử'));
  // Thần Mã là bộ riêng: cùng tên mà khoá khác
  assert.ok(c.owned.includes('hãn huyết long câu'));
  assert.ok(c.owned.includes('✦ hãn huyết long câu'));
  assert.ok(c.missing.includes('✦ xích thố'));
  assert.ok(!c.owned.some((k) => /mỗi loài|^\+/.test(k)));
  assert.equal(P.horseCollection('Mã Trường'), null);

  const row = (t) => P.horseRow(t.split('\n')[0], t);
  assert.deepEqual(row(h.rows.spare), { name: 'Liệt Hồng Mã', key: 'liệt hồng mã', than: false, dup: true, keep: false, locked: false, spare: true });
  assert.equal(row(h.rows.keep).spare, false);
  assert.deepEqual(row(h.rows.locked), { name: 'Tuyệt ảnh', key: 'tuyệt ảnh', than: false, dup: true, keep: false, locked: true, spare: true });
  assert.equal(row(h.rows.unique).dup, false);
  assert.deepEqual(row(h.rows.than), { name: 'Xích Long Câu', key: '✦ xích long câu', than: true, dup: false, keep: false, locked: false, spare: false });
  // "Tuyệt ảnh" (bộ sưu tập) và "Tuyệt Ảnh" (thẻ ngựa) là một loài
  assert.equal(P.horseKey('Tuyệt Ảnh'), P.horseKey('Tuyệt ảnh'));

  assert.deepEqual(P.stableCount(h.stableText), { used: 60, cap: 60 });
  assert.equal(P.stableCount('Ngựa trong hành trang'), null);
});

test('số viết gọn của game', () => {
  assert.equal(P.num('17.2k'), 17200);
  assert.equal(P.num('11.2k'), 11200);
  assert.equal(P.num('1.5m'), 1500000);
  assert.equal(P.num('9.435'), 9435);
  assert.equal(P.num('1341'), 1341);
  assert.equal(P.num('abc'), null);
});

test('ô KNB: số đủ và số viết gọn', () => {
  assert.deepEqual(P.knb('17.2k 🔒1113'), { knb: 17200, khoa: 1113 });
  assert.deepEqual(P.knb('1341 🔒605'), { knb: 1341, khoa: 605 });
  assert.deepEqual(P.knb('2.1k 🔒1.1k'), { knb: 2100, khoa: 1100 });
  assert.equal(P.knb(''), null);
});

test('Phó bản: "Hết lượt" là 0, "Vào x/y" cộng lại, phó bản 🔒 bỏ qua', () => {
  assert.equal(P.dungeons('2 lượt / ngày mỗi phó bản\nA\nVào 1/2\nB\nHết lượt\nC\nVào 2/2\n🔒\nD cấp 190'), '3/6');
  assert.equal(P.dungeons('2 lượt / ngày\nA\nHết lượt\nB\nHết lượt'), '0/4');
  assert.equal(P.dungeons('bảng không có nút'), ''); // không đọc được ≠ 0
});

for (const [name, fx] of fixtures) {
  test(`mẫu thật ${name}`, () => {
    // Ô KNB luôn đọc được (kể cả "11.2k")
    const k = P.knb(fx.knbTop);
    assert.ok(k, `KNB "${fx.knbTop}"`);
    assert.ok(k.knb > 0 && k.khoa >= 0);

    // Dã Tẩu: số hôm nay, lệnh bài đang có / đã dùng, tối đa mỗi ngày
    const d = P.daTau(fx.tabs.dt);
    assert.match(d.dt, /^\d+\/\d+( \(\+\d+ lệnh bài\))?$/, `Dã Tẩu "${d.dt}"`);
    assert.match(d.ldtHave, /^\d+$/);
    assert.match(d.ldtUsed, /^\d+\/\d+$/);
    assert.equal(d.dtMax, '200');

    // Phó bản: các mẫu đều chụp lúc đã đi hết → 0 lượt, mỗi phó bản 2 lượt
    const pb = P.dungeons(fx.tabs.dg);
    assert.match(pb, /^0\/\d+$/, `Phó bản "${pb}"`);
    assert.equal(+pb.split('/')[1] % 2, 0);

    assert.match(P.boat(fx.tabs.boat), /^\d\/3$/, 'Đi thuyền');
    assert.match(P.weekBoss(fx.tabs.wb), /^\d\/3$/, 'Boss tuần');
    assert.match(P.assassin(fx.tabs.st), /^\d+\/\d+$/, 'Sát Thủ');

    const shop = P.ldtShop(fx.shopKcs);
    assert.ok(shop, `Kỳ Trân Các "${fx.shopKcs}"`);
    assert.equal(shop.max, 100);
  });
}

test('mẫu thật: giá trị cụ thể', () => {
  const fx = Object.fromEntries(fixtures);
  const a1 = fx['2026-10-28-acc1.json'];
  assert.deepEqual(P.knb(a1.knbTop), { knb: 11200, khoa: 3962 });
  assert.deepEqual(P.daTau(a1.tabs.dt), { dt: '43/43 (+3 lệnh bài)', ldtHave: '0', ldtUsed: '3/160', dtMax: '200' });
  assert.equal(P.dungeons(a1.tabs.dg), '0/12');
  assert.equal(P.boat(a1.tabs.boat), '0/3');
  assert.equal(P.weekBoss(a1.tabs.wb), '0/3');
  assert.equal(P.assassin(a1.tabs.st), '0/253');
  assert.deepEqual(P.ldtShop(a1.shopKcs), { bought: 0, max: 100, have: 0 });
  assert.equal(P.boat(fx['2026-10-28-acc3.json'].tabs.boat), '2/3');
});
