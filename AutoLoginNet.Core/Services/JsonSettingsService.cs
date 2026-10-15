using AutoLoginNet.Diagnostics;
using System.Text.Json;

namespace AutoLoginNet.Services;

/// <summary>配置文件读写（JSON）。任何异常都被降级处理，保证应用始终能启动。</summary>
public interface ISettingsService
{
    /// <summary>配置文件完整路径。</summary>
    string FilePath { get; }

    /// <summary>读取配置；文件缺失或损坏时返回默认配置。</summary>
    Configuration.AppOptions Load();

    /// <summary>写回配置。</summary>
    void Save(Configuration.AppOptions options);
}

/// <summary>基于 System.Text.Json 源生成上下文的配置服务。</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private readonly ILogService _log;
    private readonly JsonSerializerOptions _options;

    public JsonSettingsService(string filePath, ILogService log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = AppJsonContext.Default,
        };
    }

    public string FilePath { get; }

    public Configuration.AppOptions Load()
    {
        var defaults = new Configuration.AppOptions();
        defaults.Normalize();

        try
        {
            if (!File.Exists(FilePath))
            {
                return defaults;
            }

            string json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppOptions);
            if (loaded is null)
            {
                return defaults;
            }

            // 缺失的分组与越界的数值在此收敛为合法值
            Normalize(loaded, defaults);
            return loaded;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取配置文件失败，已回退默认配置：{ex.Message}", "配置");
            return defaults;
        }
    }

    public void Save(Configuration.AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            options.Normalize();

            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(options, AppJsonContext.Default.AppOptions);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);

            _log.Info($"配置已保存到 {FilePath}", "配置");
        }
        catch (Exception ex)
        {
            _log.Error($"保存配置失败：{ex.Message}", "配置");
        }
    }

    private static void Normalize(Configuration.AppOptions loaded, Configuration.AppOptions defaults)
    {
        // 反序列化时缺失的分组会是 null，先用默认实例补齐再统一收敛
        loaded.Auth ??= defaults.Auth;
        loaded.Probes ??= defaults.Probes;
        loaded.Logging ??= defaults.Logging;
        loaded.Startup ??= defaults.Startup;
        loaded.Normalize();
    }
}
