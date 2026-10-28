using System.Diagnostics;

namespace VLMulti.Tests;

// Phần đọc số trong trang (src/VLMulti.Core/Page/parse.js) được test bằng chữ chép từ game thật (tests/agent).
// Test này chạy bộ test đó bằng Node để `dotnet test` (và publish*.ps1) bắt được lỗi đọc trước khi đóng gói.
// Máy không có Node → bỏ qua.
public class AgentParserTests
{
    [Fact]
    public void Page_parsers_pass_on_real_game_fixtures()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !root.EnumerateFiles("*.sln").Any()) root = root.Parent;
        Assert.NotNull(root);
        var testFile = Path.Combine(root!.FullName, "tests", "agent", "parse.test.mjs");
        Assert.True(File.Exists(testFile), testFile);

        Process? node;
        try
        {
            node = Process.Start(new ProcessStartInfo("node", $"--test \"{testFile}\"")
            {
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return; // không có Node
        }
        Assert.NotNull(node);
        var output = node!.StandardOutput.ReadToEnd() + node.StandardError.ReadToEnd();
        node.WaitForExit(60_000);
        Assert.True(node.ExitCode == 0, output);
    }
}
