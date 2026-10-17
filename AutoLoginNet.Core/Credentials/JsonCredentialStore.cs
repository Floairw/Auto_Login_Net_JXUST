using AutoLoginNet.Auth;
using AutoLoginNet.Diagnostics;
using System.Text.Json;

namespace AutoLoginNet.Credentials;

/// <summary>
/// 基于 JSON + <see cref="ICredentialProtector"/> 的凭据存储。
/// 只支持当前格式版本；旧格式会被拒绝并提示重新登录，避免静默降级带来的安全问题。
/// </summary>
public sealed class JsonCredentialStore : ICredentialStore
{
    private readonly ICredentialProtector _protector;
    private readonly ILogService _log;

    /// <summary>保护 <see cref="_lastSaved"/>。凭据保存可能来自界面线程或自启后台线程。</summary>
    private readonly object _sync = new();

    /// <summary>
    /// 上一次写入磁盘的明文三元组（账号、明文密码、运营商）。
    /// 只存在于内存中，仅用于判断「内容未变则不必重复落盘」，不会持久化。
    /// </summary>
    private (string UserName, string Password, OperatorKind Operator)? _lastSaved;

    public JsonCredentialStore(string filePath, ICredentialProtector protector, ILogService log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    public LoginCredential? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        CredentialRecord? record;
        try
        {
            string json = File.ReadAllText(FilePath);
            record = JsonSerializer.Deserialize(json, AppJsonContext.Default.CredentialRecord);
        }
        catch (Exception ex)
        {
            _log.Error($"读取凭据文件失败：{ex.Message}", "凭据");
            return null;
        }

        // 先判版本再判内容：旧格式文件缺少 userName 字段，先报版本问题才能给出
        // 「请重新登录以保存新格式」这个真正有用的提示
        if (record is not null && record.SchemaVersion != CredentialRecord.CurrentSchemaVersion)
        {
            _log.Warn(
                $"凭据格式版本为 {record.SchemaVersion}，当前仅支持版本 {CredentialRecord.CurrentSchemaVersion}，请重新登录以保存新格式",
                "凭据");
            return null;
        }

        if (record is null || string.IsNullOrWhiteSpace(record.UserName))
        {
            _log.Warn("凭据文件内容无效，已忽略", "凭据");
            return null;
        }

        string password;
        try
        {
            password = _protector.Unprotect(record.Password);
        }
        catch (Exception ex)
        {
            _log.Error($"凭据解密失败（可能由其他用户或机器创建）：{ex.Message}", "凭据");
            return null;
        }

        return new LoginCredential(record.UserName, password, record.Operator, record.SavedAt);
    }

    public void Save(string userName, string password, OperatorKind @operator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);

        string trimmedUserName = userName.Trim();

        // 内容未变时不重复写入：每次登录成功都会调用 Save，
        // 若每次都落盘会产生大量无意义的「凭据已保存」日志与磁盘写入。
        lock (_sync)
        {
            if (_lastSaved is { } last
                && string.Equals(last.UserName, trimmedUserName, StringComparison.Ordinal)
                && string.Equals(last.Password, password, StringComparison.Ordinal)
                && last.Operator == @operator)
            {
                return;
            }
        }

        try
        {
            var record = new CredentialRecord
            {
                SchemaVersion = CredentialRecord.CurrentSchemaVersion,
                UserName = trimmedUserName,
                Password = _protector.Protect(password),
                Operator = @operator,
                SavedAt = DateTimeOffset.Now,
            };

            string json = JsonSerializer.Serialize(record, AppJsonContext.Default.CredentialRecord);
            AtomicFile.WriteAllText(FilePath, json);

            lock (_sync)
            {
                _lastSaved = (trimmedUserName, password, @operator);
            }

            _log.Info($"凭据已保存（账号 {record.UserName}，运营商 {@operator.ToDisplayName()}）", "凭据");
        }
        catch (Exception ex)
        {
            _log.Error($"保存凭据失败：{ex.Message}", "凭据");
        }
    }

    public void Clear()
    {
        try
        {
            lock (_sync)
            {
                _lastSaved = null;
            }

            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                _log.Info("已清除保存的凭据", "凭据");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"清除凭据失败：{ex.Message}", "凭据");
        }
    }
}
