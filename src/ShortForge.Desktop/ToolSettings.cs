using System.IO;
using System.Text.Json;
using ShortForge.Core;
using ShortForge.Infrastructure;

namespace ShortForge.Desktop;

internal sealed class ToolSettings(string dataDirectory)
{
    private string SettingsFile => Path.Combine(dataDirectory, "tools.json");

    public void UseAutomaticSearch() => File.Delete(SettingsFile);

    public string? ReadDirectory()
    {
        try
        {
            var directory = JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsFile));
            if (directory != null && Path.IsPathFullyQualified(directory)) return directory;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { }
        throw new UserError("tool_settings", "Сохранённую настройку папки FFmpeg не удалось прочитать. Выберите папку заново или включите автоматический поиск.");
    }

    public string? Resolve(string name) => Resolve(name, ReadDirectory());

    public static string? Resolve(string name, string? selectedDirectory)
    {
        if (selectedDirectory != null)
        {
            var selected = Path.Combine(selectedDirectory, name + ".exe");
            return File.Exists(selected) ? selected : null;
        }
        var folders = new[] { Environment.GetEnvironmentVariable("SHORTFORGE_FFMPEG_BIN"),
            Path.Combine(AppContext.BaseDirectory, "tools") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return folders.Where(d => !string.IsNullOrWhiteSpace(d) && Path.IsPathFullyQualified(d))
            .Select(d => Path.Combine(d!, name + ".exe")).FirstOrDefault(File.Exists);
    }

    public async Task SetAsync(string directory, CancellationToken token)
    {
        var probe = Path.Combine(directory, "ffprobe.exe");
        if (!File.Exists(probe) || await Library.ToolVersionAsync(probe, token) is not string version ||
            !version.StartsWith("ffprobe version ", StringComparison.OrdinalIgnoreCase))
            throw new UserError("invalid_tools", "В выбранной папке нужен работающий ffprobe.exe.");
        var temporary = Path.Combine(dataDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(directory), token);
            File.Move(temporary, SettingsFile, true);
        }
        finally { File.Delete(temporary); }
    }
}
