using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// 统一的 JSON 写出选项：缩进 + 不转义非 ASCII 字符。
/// System.Text.Json 默认把中文全部转成 \uXXXX 转义（合法 JSON 但人眼不可读，用户会以为「文件里没规则/没中文」），
/// 必须用 UnsafeRelaxedJsonEscaping 让中文按 UTF-8 原样写出；文件均以 Encoding.UTF8 落盘，无注入风险。
/// </summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 原子写：先写 .tmp 再 File.Move 覆盖目标，避免写一半进程被杀 / 插件卸载导致文件截断、
    /// 下次读取时解析失败、再用空词典覆盖写回造成全部沉淀译文丢失。
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
