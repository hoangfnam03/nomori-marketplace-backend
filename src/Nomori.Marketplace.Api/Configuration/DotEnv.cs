namespace Nomori.Marketplace.Api.Configuration;

/// <summary>
/// Reads a local <c>.env</c> file (KEY=VALUE per line, git-ignored) into the process environment before configuration is built, so local
/// secrets live next to the source instead of in the machine's environment. The file is looked for from the working directory up to the
/// repository root (the folder with the solution). A variable already set in the real environment wins, so a server or CI can override it.
/// Keys use the usual double underscore for sections: <c>Shipping__Ghn__Token=...</c>.
/// </summary>
internal static class DotEnv
{
    public static void Load(string fileName = ".env")
    {
        var path = Find(fileName);
        if (path is null) return;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line["export ".Length..].TrimStart();

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];

            if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string? Find(string fileName)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            // The repository root is as far as it goes: a .env higher up belongs to something else.
            if (dir.EnumerateFiles("*.sln").Any() || Directory.Exists(Path.Combine(dir.FullName, ".git"))) return null;
        }
        return null;
    }
}
