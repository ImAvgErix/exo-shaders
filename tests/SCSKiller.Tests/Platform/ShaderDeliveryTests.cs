using SCSKiller.Core;
using SCSKiller.Core.Vendors;
using Xunit;

namespace SCSKiller.Tests.Platform;

/// <summary>
/// Whether a PC can take Windows' precompiled shaders (Advanced Shader Delivery). It fails if a PC without the API or
/// Gaming Services is called ready, a GTX card or an NVIDIA driver before 621.01 is called ready, a ready PC is turned
/// away, or Gaming Services' version is misread from its service path.
/// </summary>
public sealed class ShaderDeliveryTests
{
    static GpuInfo Gpu(GpuVendor vendor, string name, string driver) => new(vendor, name, driver, 1, 8L << 30);

    static readonly Version Services = new(37, 113, 11003, 0);

    [Fact]
    public void ReadyOnlyWithEveryPart()
    {
        var rtx = Gpu(GpuVendor.Nvidia, "NVIDIA GeForce RTX 3070", "621.01");
        Assert.True(ShaderDelivery.Check(rtx, true, Services).Works);
        Assert.True(ShaderDelivery.Check(rtx with { DriverVersion = "625.40" }, true, new Version(38, 0)).Works);
        Assert.False(ShaderDelivery.Check(rtx, false, Services).Works);
        Assert.Equal("Needs Gaming Services from the Microsoft Store", ShaderDelivery.Check(rtx, true, null).Text);
        Assert.False(ShaderDelivery.Check(rtx, true, new Version(37, 112, 0, 0)).Works);
        Assert.Equal("Needs NVIDIA driver 621.01 or newer", ShaderDelivery.Check(rtx with { DriverVersion = "617.70" }, true, Services).Text);
        // DXGI's Windows form of the same drivers
        Assert.True(ShaderDelivery.Check(rtx with { DriverVersion = "32.0.16.2101" }, true, Services).Works);
        Assert.Equal("Needs NVIDIA driver 621.01 or newer", ShaderDelivery.Check(rtx with { DriverVersion = "32.0.16.1770" }, true, Services).Text);
        Assert.Equal("Not on GTX graphics cards", ShaderDelivery.Check(Gpu(GpuVendor.Nvidia, "NVIDIA GeForce GTX 1080 Ti", "621.01"), true, Services).Text);
        Assert.True(ShaderDelivery.Check(Gpu(GpuVendor.Amd, "AMD Radeon RX 9070 XT", "32.0.21037.1004"), true, Services).Works);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\WindowsApps\Microsoft.GamingServices_37.113.11003.0_x64__8wekyb3d8bbwe\GamingServices.exe""", "37.113.11003.0")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.GamingServices_38.2.1001.0_x64__8wekyb3d8bbwe\GamingServices.exe", "38.2.1001.0")]
    [InlineData(@"C:\Windows\System32\svchost.exe -k GamingServices", "0.0")]
    public void ReadsGamingServicesVersion(string image, string version) =>
        Assert.Equal(Version.Parse(version), ShaderDelivery.VersionInPath(image));
}
