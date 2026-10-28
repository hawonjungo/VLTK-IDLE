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
const fixtures = readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => [f, JSON.parse(readFileSync(new URL(f, dir), 'utf8'))]);

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
