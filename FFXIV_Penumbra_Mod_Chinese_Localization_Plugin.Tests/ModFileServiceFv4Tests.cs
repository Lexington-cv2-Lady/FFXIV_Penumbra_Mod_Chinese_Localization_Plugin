using System;
using System.IO;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：ModFileService 的读取范围（审查方第三轮【低】③）。
/// FV4（meta.json 顶层 Groups）下 Penumbra 只读 meta.json，遗留的 group_*.json 纯属冗余：
/// 必须跳过——否则同一分组被翻译两遍，并会延续历史遗留的「同名不同分隔符」重复文件。
/// 非 FV4（Mod.Groups 包装 或 完全没有 meta）时，group_*.json 仍是唯一/主要翻译来源，必须照读。
/// </summary>
public class ModFileServiceFv4Tests
{
    private static string NewModDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmh_fv4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Fv4_TopLevelGroups_SkipsLegacyGroupFiles()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"),
                "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"G\",\"Options\":[{\"Name\":\"O\"}]}]}");
            File.WriteAllText(Path.Combine(dir, "group_abc.json"),
                "{\"Name\":\"Legacy\",\"Options\":[{\"Name\":\"O\"}]}");

            var files = new ModFileService().ReadModFiles(dir);

            Assert.Single(files);
            Assert.True(files[0].IsMeta);
            Assert.Equal("meta.json", files[0].FileName);
            Assert.False(files[0].MetaWrapped);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WrappedLegacyMeta_StillReadsGroupFiles()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"),
                "{\"FileVersion\":3,\"Mod\":{\"Groups\":[{\"Name\":\"G\",\"Options\":[]}]}}");
            File.WriteAllText(Path.Combine(dir, "group_abc.json"),
                "{\"Name\":\"Legacy\",\"Options\":[{\"Name\":\"O\"}]}");

            var files = new ModFileService().ReadModFiles(dir);

            Assert.Equal(2, files.Count); // 旧格式：meta + group 都要读
            Assert.Contains(files, f => f.IsMeta && f.MetaWrapped);
            Assert.Contains(files, f => !f.IsMeta && f.FileName == "group_abc.json");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoMetaAtAll_StillReadsGroupFiles()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "group_abc.json"), "{\"Name\":\"Legacy\",\"Options\":[]}");

            var files = new ModFileService().ReadModFiles(dir);

            Assert.Single(files);
            Assert.False(files[0].IsMeta);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MetaWithoutGroups_IsNotFv4_SoGroupFilesStillRead()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"), "{\"FileVersion\":4,\"Name\":\"no groups\"}");
            File.WriteAllText(Path.Combine(dir, "group_abc.json"), "{\"Name\":\"Legacy\",\"Options\":[]}");

            var files = new ModFileService().ReadModFiles(dir);

            Assert.Single(files); // meta 无 Groups → 不作为 FV4 源头，group 仍被读取
            Assert.False(files[0].IsMeta);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
