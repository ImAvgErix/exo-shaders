namespace SCSKiller.Core;

public sealed record GpuInfo(GpuVendor Vendor, string Name, string DriverVersion, long AdapterLuid, ulong DedicatedVideoMemory);
