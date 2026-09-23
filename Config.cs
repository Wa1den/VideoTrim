using System.IO;
using System.Text.Json;

namespace VideoTrim;

/// <summary>
/// What the window remembers between runs. Written on exit; a broken or foreign file
/// gives the defaults instead of a failed start.
/// </summary>
public sealed class Config
{
    public string? OpenDir { get; set; }
    public string? SaveDir { get; set; }
    public bool CopyStreams { get; set; }
    public string? Encoder { get; set; }
    public bool ByQuality { get; set; }
    public int QualityLevel { get; set; } = 1;
    public bool CheckUpdates { get; set; }

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoTrim", "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch (Exception) { }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
