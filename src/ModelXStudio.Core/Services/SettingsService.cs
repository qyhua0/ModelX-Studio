// =====================================================================
// 作者：zhenhua
// 功能：settings.json 的读写：读取失败自动回退默认值不阻塞启动，写入采用 .tmp + 原子改名，
//       向全应用提供 AppSettings.Current。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Text.Json;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

/// <summary>读写 settings.json。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(AppSettings.SettingsFilePath))
            {
                var json = File.ReadAllText(AppSettings.SettingsFilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch
        {
            // 设置损坏时回退默认值，不阻塞启动
            Current = new AppSettings();
        }
        Current.EnsureDirectories();
    }

    /// <summary>
    /// 保存设置（先写 .tmp 再原子改名）。返回 false 表示写盘失败，调用方可选择提示用户；
    /// 不抛异常是为了不让设置写盘失败干扰启动/模型加载流程。
    /// </summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.AppDataDirectory);
            var tmpPath = AppSettings.SettingsFilePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(Current, JsonOpts));
            File.Move(tmpPath, AppSettings.SettingsFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 设置保存失败：{ex.Message}");
            return false;
        }
    }
}
