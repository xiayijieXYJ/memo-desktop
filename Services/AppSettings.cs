using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memo.Services;

/// <summary>
/// 窗口位置、透明度等界面偏好，落盘到 %APPDATA%\Memo\settings.json。
/// 故意不放进数据库：UI 偏好和业务数据分开，卸载/重置数据时互不影响。
/// </summary>
public sealed class AppSettings
{
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } = 300;
    public double WindowHeight { get; set; } = 420;
    public bool HasPosition { get; set; }

    /// <summary>0.35 ~ 1.0。</summary>
    public double Opacity { get; set; } = 0.94;

    public bool IsPinned { get; set; } = true;
    public bool HideDone { get; set; }

    /// <summary>"Dark" / "Light" / "Sepia"。空/脏数据由 ThemeManager.Normalize 回落深色。</summary>
    public string Theme { get; set; } = ThemeManager.Dark;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load()
    {
        // 首次运行返回默认值；文件损坏时也回落默认值，不让一个坏配置挡住启动。
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.WindowWidth = Math.Clamp(loaded.WindowWidth, 240, 1200);
                    loaded.WindowHeight = Math.Clamp(loaded.WindowHeight, 200, 1400);
                    loaded.Opacity = Math.Clamp(loaded.Opacity, 0.35, 1.0);
                    loaded.Theme = ThemeManager.Normalize(loaded.Theme);
                    return loaded;
                }
            }
        }
        catch
        {
            // ignored
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 设置保存失败不应打断使用
        }
    }
}

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Memo");

    public static string DatabasePath { get; } = Path.Combine(DataDir, "memo.db");

    /// <summary>自动备份目录，每次启动做一份快照，保留最近 20 份。</summary>
    public static string BackupDir { get; } = Path.Combine(DataDir, "backups");

    /// <summary>删除审计日志：记录每次删除的调用来源，出问题时能回溯是谁删的。</summary>
    public static string AuditLogPath { get; } = Path.Combine(DataDir, "delete-audit.log");
}
