namespace AutoLoginNet.Services;

/// <summary>命令行参数的构造与转义辅助。</summary>
public static class NetUtils
{
    /// <summary>用双引号包裹参数，并转义内部引号。</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
