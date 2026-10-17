namespace AutoLoginNet;

/// <summary>原子写文件：先写同目录临时文件再替换，避免中途崩溃留下半截内容。</summary>
internal static class AtomicFile
{
    /// <summary>把内容原子写入目标路径（必要时先创建上级目录）。</summary>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
