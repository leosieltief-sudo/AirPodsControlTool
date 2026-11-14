using AirPodsControl.Core.Bridge;
using Xunit;

namespace AirPodsControl.Tests;

public class WslConfigFileTests
{
    private const string Dir = @"C:\Users\u\AppData\Local\AirPodsControl\kernel";
    private const string K = Dir + @"\bzImage";
    private const string M = Dir + @"\modules.vhdx";

    [Fact]
    public void Creates_section_when_file_absent()
    {
        var t = WslConfigFile.SetKernel(null, K, M, Dir);
        Assert.Equal("[wsl2]\r\n" + WslConfigFile.Marker + "\r\n" +
                     @"kernel=C:\\Users\\u\\AppData\\Local\\AirPodsControl\\kernel\\bzImage" + "\r\n" +
                     @"kernelModules=C:\\Users\\u\\AppData\\Local\\AirPodsControl\\kernel\\modules.vhdx" + "\r\n", t);
        Assert.Equal(WslKernelSetting.Ours, WslConfigFile.GetKernel(t, Dir, out var p));
        Assert.Equal(K, p);
    }

    [Fact]
    public void Inserts_into_existing_wsl2_section_and_preserves_everything_else()
    {
        const string orig = "# my settings\n[wsl2]\nmemory=8GB\nswap=0\n\n[experimental]\nautoMemoryReclaim=gradual\n";
        var t = WslConfigFile.SetKernel(orig, K, M, Dir);
        Assert.Contains("memory=8GB\n", t);
        Assert.Contains("[experimental]\nautoMemoryReclaim=gradual\n", t);
        Assert.StartsWith("# my settings\n[wsl2]\n" + WslConfigFile.Marker + "\nkernel=", t);
        Assert.DoesNotContain("\r\n", t);   // keeps the file's line endings
        // removing restores the original byte-for-byte
        Assert.Equal(orig, WslConfigFile.RemoveKernel(t, Dir));
    }

    [Fact]
    public void Install_is_idempotent()
    {
        var once = WslConfigFile.SetKernel("[wsl2]\nmemory=4GB\n", K, M, Dir);
        var twice = WslConfigFile.SetKernel(once, K, M, Dir);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Never_overwrites_a_foreign_kernel()
    {
        const string orig = "[wsl2]\nkernel=C:\\\\kernels\\\\my-bzImage\n";
        Assert.Equal(WslKernelSetting.Foreign, WslConfigFile.GetKernel(orig, Dir, out var p));
        Assert.Equal(@"C:\kernels\my-bzImage", p);
        Assert.Throws<InvalidOperationException>(() => WslConfigFile.SetKernel(orig, K, M, Dir));
        Assert.Equal(orig, WslConfigFile.RemoveKernel(orig, Dir));   // remove leaves it alone too
    }

    [Fact]
    public void Remove_drops_section_we_created_and_returns_empty()
    {
        var t = WslConfigFile.SetKernel(null, K, M, Dir);
        Assert.Equal("", WslConfigFile.RemoveKernel(t, Dir));
    }

    [Fact]
    public void Remove_keeps_section_before_another_one()
    {
        var t = WslConfigFile.SetKernel("[experimental]\nsparseVhd=true\n", K, M, Dir);
        Assert.Equal("[experimental]\nsparseVhd=true\n", WslConfigFile.RemoveKernel(t, Dir));
    }

    [Fact]
    public void Kernel_key_in_other_section_is_ignored()
    {
        Assert.Equal(WslKernelSetting.Stock, WslConfigFile.GetKernel("[experimental]\nkernel=C:\\\\x\n", Dir, out _));
        Assert.Equal(WslKernelSetting.Stock, WslConfigFile.GetKernel("", Dir, out _));
    }

    [Theory]
    [InlineData("\"C:\\\\a b\\\\bzImage\"", @"C:\a b\bzImage")]
    [InlineData("C:/kern/bzImage", "C:/kern/bzImage")]
    [InlineData(@"C:\\k\\bz  # comment", @"C:\k\bz")]
    public void Unescape_values(string raw, string expected) => Assert.Equal(expected, WslConfigFile.Unescape(raw));

    [Fact]
    public void Forward_slash_path_inside_our_dir_counts_as_ours()
    {
        var t = "[wsl2]\nkernel=" + Dir.Replace('\\', '/') + "/bzImage\n";
        Assert.Equal(WslKernelSetting.Ours, WslConfigFile.GetKernel(t, Dir, out _));
        Assert.Equal("", WslConfigFile.RemoveKernel(t, Dir));
    }
}

public class MediaTekDetectionTests
{
    [Theory]
    [InlineData("13d3:3622", "Bluetooth adapter", null, true)]     // this PC (MT7922, ID added by our kernel patch)
    [InlineData("13D3:3568", "Generic", null, true)]               // MT7922A from the btusb table
    [InlineData("0e8d:0608", "x", null, true)]                     // MediaTek's own VID
    [InlineData("0e8d:9999", "x", null, true)]
    [InlineData("13d3:3529", "Realtek Bluetooth", "Realtek", false)] // same vendor (IMC Networks), Realtek chip
    [InlineData("8087:0026", "Intel(R) Wireless Bluetooth(R)", "Intel", false)]
    [InlineData("1234:5678", "MediaTek Bluetooth Adapter", null, true)] // unknown ID, name tells
    [InlineData("1234:5678", "Adapter", "MediaTek Inc.", true)]
    public void Detects_mediatek(string vidPid, string name, string? mfg, bool expected) =>
        Assert.Equal(expected, MediaTekUsbIds.IsMediaTek(vidPid, name, mfg));

    [Fact]
    public void Generated_list_is_not_empty_and_lowercase()
    {
        Assert.True(MediaTekUsbIds.Known.Count >= 30);
        Assert.All(MediaTekUsbIds.Known, id => Assert.Matches("^[0-9a-f]{4}:[0-9a-f]{4}$", id));
    }
}
