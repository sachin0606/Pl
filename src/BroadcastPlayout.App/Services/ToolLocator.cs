namespace BroadcastPlayout.Services;

public static class ToolLocator
{
    public static string? FindSuiteRoot()
    {
        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "suite-root.txt");
        try
        {
            if (File.Exists(marker))
            {
                var saved = File.ReadAllText(marker).Trim();
                if (File.Exists(Path.Combine(saved, "BroadcastPlayout.sln"))) return saved;
            }
        }
        catch { }
        foreach (var seed in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(seed);
            for (var i = 0; i < 14 && dir is not null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "BroadcastPlayout.sln"))) return dir.FullName;
        }
        return null;
    }

    public static string? FindTool(string name)
    {
        var root = FindSuiteRoot();
        if (root is not null)
        {
            var source = Path.Combine(root, "tools", name);
            if (File.Exists(source)) return source;
        }
        var besideExe = Path.Combine(AppContext.BaseDirectory, "tools", name);
        return File.Exists(besideExe) ? besideExe : null;
    }
}
