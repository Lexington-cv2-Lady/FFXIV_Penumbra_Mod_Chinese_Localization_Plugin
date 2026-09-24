using System;
using System.IO;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：JsonFile.WriteAtomic 原子写（通用母本 B.7 红线）。
/// 先写 .tmp 再 File.Move 覆盖目标，必须保证：目标内容完整、无 .tmp 残留、可覆盖旧文件。
/// </summary>
public class JsonFileAtomicWriteTests
{
    [Fact]
    public void WriteAtomic_LeavesNoTempFile_AndContentIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "d.json");
        try
        {
            JsonFile.WriteAtomic(path, "{\"a\":1}");

            Assert.True(File.Exists(path));                    // 目标存在
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));  // 内容完整
            Assert.False(File.Exists(path + ".tmp"));          // 无残留 .tmp（原子写标志）
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteAtomic_OverwritesExistingFile_WithoutTempLeftover()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "d.json");
        try
        {
            File.WriteAllText(path, "old");
            JsonFile.WriteAtomic(path, "new");

            Assert.Equal("new", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteAtomic_CreatesParentDirectory_WhenMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmh_test_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "nested", "d.json"); // 父目录不存在
        try
        {
            JsonFile.WriteAtomic(path, "x");
            Assert.True(File.Exists(path));
            Assert.Equal("x", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
