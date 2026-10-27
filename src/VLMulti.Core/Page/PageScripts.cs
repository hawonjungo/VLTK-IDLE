namespace VLMulti.Core.Page;

public static class PageScripts
{
    private static readonly Lazy<string> AgentScript = new(() => Load("agent.js"));

    // Script nạp vào trang game của mỗi khung (xem agent.js)
    public static string Agent => AgentScript.Value;

    private static string Load(string name)
    {
        using var stream = typeof(PageScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
