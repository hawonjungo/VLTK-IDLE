using System.Text.Json;
using VLMulti.Core.Sessions;
using Xunit;

namespace VLMulti.Tests;

public class HorseTransferTests
{
    private static HorseAccount Acc(string id, string name, string[] missing, params HorseSpare[] spares) =>
        new(id, name, "ok", 40, 62, 1, 14, missing, spares, 0, Array.Empty<string>(), 0, false, false);

    private static HorseSpare Spare(string uid, string key, string where = "bag", bool locked = false) => new(uid, key, key, where, locked);

    [Fact]
    public void Plan_gives_missing_species_from_other_accounts_spares()
    {
        var a = Acc("acc1", "A", new[] { "sư tử" }, Spare("1", "tuyệt ảnh"));
        var b = Acc("acc2", "B", new[] { "tuyệt ảnh", "phi vân" }, Spare("2", "sư tử"));
        var plan = HorsePlanner.Plan(new[] { a, b });
        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, p => p.FromId == "acc2" && p.ToName == "A" && p.Horse.Uid == "2");
        Assert.Contains(plan, p => p.FromId == "acc1" && p.ToName == "B" && p.Horse.Uid == "1");
    }

    [Fact]
    public void Plan_uses_each_spare_once_and_prefers_bag_and_unlocked()
    {
        var donor = Acc("acc1", "A", Array.Empty<string>(),
            Spare("st", "xích thố", "st"), Spare("lk", "xích thố", "bag", locked: true), Spare("ok", "xích thố"));
        var r1 = Acc("acc2", "B", new[] { "xích thố" });
        var r2 = Acc("acc3", "C", new[] { "xích thố" });
        var r3 = Acc("acc4", "D", new[] { "xích thố" });
        var r4 = Acc("acc5", "E", new[] { "xích thố" });
        var plan = HorsePlanner.Plan(new[] { donor, r1, r2, r3, r4 });
        Assert.Equal(new[] { "ok", "lk", "st" }, plan.Select(p => p.Horse.Uid));
        Assert.Equal(new[] { "B", "C", "D" }, plan.Select(p => p.ToName));
    }

    [Fact]
    public void Plan_never_sends_to_itself_or_to_accounts_without_a_name_or_a_scan()
    {
        var self = Acc("acc1", "A", new[] { "sư tử" }, Spare("1", "sư tử"));
        var noName = Acc("acc2", "", new[] { "sư tử" });
        var failed = Acc("acc3", "C", new[] { "sư tử" }) with { Result = "timeout" };
        Assert.Empty(HorsePlanner.Plan(new[] { self, noName, failed }));
    }

    [Fact]
    public void Than_ma_is_a_separate_species()
    {
        var a = Acc("acc1", "A", Array.Empty<string>(), Spare("1", "xích long câu"));
        var b = Acc("acc2", "B", new[] { "✦ xích long câu" });
        Assert.Empty(HorsePlanner.Plan(new[] { a, b }));
    }

    [Fact]
    public void Sales_only_unlocked_spares_nobody_needs_and_not_being_moved()
    {
        var a = Acc("acc1", "A", new[] { "sư tử" },
            Spare("1", "hoàng mã"),                 // không ai thiếu → bán
            Spare("2", "tuyệt ảnh"),                // B thiếu → không bán (đang chuyển)
            Spare("3", "tuyệt ảnh"),                // B thiếu → không bán (con thứ hai vẫn có người cần)
            Spare("4", "bạch mã", locked: true));   // khoá → không bán
        var b = Acc("acc2", "B", new[] { "tuyệt ảnh" }, Spare("5", "sư tử"), Spare("6", "hắc mã"));
        var plan = HorsePlanner.Plan(new[] { a, b });
        var sales = HorsePlanner.Sales(new[] { a, b }, plan, new[] { "A", "B" });
        Assert.Equal(new[] { "1", "6" }, sales.Select(s => s.Horse.Uid).OrderBy(x => x));
    }

    [Fact]
    public void No_sales_unless_every_own_character_was_scanned()
    {
        var a = Acc("acc1", "A", Array.Empty<string>(), Spare("1", "hoàng mã"));
        var b = Acc("acc2", "B", Array.Empty<string>()) with { Result = "timeout" };
        Assert.Empty(HorsePlanner.Sales(new[] { a, b }, new List<HorseMove>(), new[] { "A", "B" }));
        Assert.Empty(HorsePlanner.Sales(new[] { a }, new List<HorseMove>(), new[] { "A", "C" }));
        Assert.Single(HorsePlanner.Sales(new[] { a }, new List<HorseMove>(), new[] { "A" }));
    }

    [Fact]
    public void Scan_result_is_read_from_page_json()
    {
        var json = JsonDocument.Parse("""
            {"t":"horseResult","r":"ok","op":"scan","opened":10,"stored":"Hắc Mã,Siêu Quang","mails":1,"sent":"","failed":"",
             "coll":{"have":40,"total":62,"thanHave":1,"thanTotal":14,"owned":["hoàng mã"],"missing":["sư tử","✦ xích thố"]},
             "spares":[{"uid":"461171","key":"túc sương","name":"Túc Sương","where":"bag","locked":true}],
             "stable":{"used":60,"cap":60},"stableFull":true}
            """).RootElement;
        var acc = HorseAccount.From("acc1", "A", json);
        Assert.Equal("ok", acc.Result);
        Assert.Equal((40, 62, 1, 14), (acc.Have, acc.Total, acc.ThanHave, acc.ThanTotal));
        Assert.Equal(new[] { "sư tử", "✦ xích thố" }, acc.Missing);
        Assert.Equal(new[] { "Hắc Mã", "Siêu Quang" }, acc.Stored);
        Assert.Equal(10, acc.Opened);
        Assert.True(acc.StableFull);
        var s = Assert.Single(acc.Spares);
        Assert.Equal(("461171", "túc sương", "bag", true), (s.Uid, s.Key, s.Where, s.Locked));
    }

    // Trang giả: trả lời theo kịch bản, ghi lại các tin đã nhận
    private sealed class FakePage
    {
        public readonly List<JsonElement> Sent = new();
        public Func<JsonElement, string>? Reply;
        public HorseMember Member(HorseCoordinator hc, string id, string name) => new(id, name, msg =>
        {
            var el = JsonSerializer.SerializeToElement(msg);
            lock (Sent) Sent.Add(el);
            var r = Reply!(el.GetProperty("arg"));
            _ = Task.Run(() => hc.OnResult(id, JsonDocument.Parse(r).RootElement));
        }, _ => { });
    }

    [Fact]
    public async Task Scan_then_send_mails_only_own_names_and_receivers_take()
    {
        HorseCoordinator.BusyRetryDelay = TimeSpan.FromMilliseconds(10);
        var hc = new HorseCoordinator();
        var p1 = new FakePage(); var p2 = new FakePage();
        var scans = 0;
        p1.Reply = arg => arg.GetProperty("op").GetString() switch
        {
            "scan" when ++scans == 1 => """{"r":"busy"}""",
            "scan" => """{"r":"ok","coll":{"have":40,"total":62,"missing":["sư tử"]},"spares":[{"uid":"9","key":"tuyệt ảnh","name":"Tuyệt ảnh","where":"st","locked":true}]}""",
            "send" => """{"r":"ok","sent":"9","failed":""}""",
            _ => """{"r":"ok","mails":0}""",
        };
        p2.Reply = arg => arg.GetProperty("op").GetString() switch
        {
            "scan" => """{"r":"ok","coll":{"have":34,"total":62,"missing":["tuyệt ảnh"]},"spares":[]}""",
            "take" => """{"r":"ok","mails":1,"stored":"Tuyệt ảnh","coll":{"have":35,"total":62,"missing":[]}}""",
            _ => """{"r":"ok"}""",
        };
        var members = new[] { p1.Member(hc, "acc1", "A"), p2.Member(hc, "acc2", "B") };
        var names = new[] { "A", "B" };

        Assert.True(await hc.ScanAsync(members, names, openChests: true));
        Assert.Equal("ready", hc.Phase);
        var move = Assert.Single(hc.Plan);
        Assert.Equal(("acc1", "B", "9"), (move.FromId, move.ToName, move.Horse.Uid));
        // Trang bận lần đầu → thử lại
        Assert.Equal(2, p1.Sent.Count(e => e.GetProperty("arg").GetProperty("op").GetString() == "scan"));

        Assert.True(await hc.SendAsync(members, names));
        Assert.Equal("done", hc.Phase);
        var send = p1.Sent.Single(e => e.GetProperty("arg").GetProperty("op").GetString() == "send").GetProperty("arg");
        Assert.Equal("B", send.GetProperty("to").GetString());
        Assert.Equal(new[] { "A", "B" }, send.GetProperty("allowed").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains(p2.Sent, e => e.GetProperty("arg").GetProperty("op").GetString() == "take");
        Assert.Contains(hc.Lines, l => l.Contains("A → B: Tuyệt ảnh"));
        Assert.Equal(35, hc.Accounts.Single(a => a.Id == "acc2").Have);
        // Không có kế hoạch nữa → bấm Chuyển lần nữa không làm gì
        Assert.False(await hc.SendAsync(members, names));
    }
}
