using ManifestApp.Core;
using Xunit;

namespace ManifestApp.Core.Tests;

public sealed class SafePathUnderRootTests
{
    [Fact]
    public void TryResolve_accepts_relative_file_under_root()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gamegen-safe-path-test"));
        Directory.CreateDirectory(root);

        try
        {
            Assert.True(SafePathUnderRoot.TryResolve(root, "data/file.dll", out var full));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "data", "file.dll")), full);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolve_rejects_parent_traversal()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gamegen-safe-path-test2"));
        Directory.CreateDirectory(root);

        try
        {
            Assert.False(SafePathUnderRoot.TryResolve(root, "..\\escape.txt", out _));
            Assert.False(SafePathUnderRoot.TryResolve(root, "..\\..\\Windows\\System32\\evil.dll", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
