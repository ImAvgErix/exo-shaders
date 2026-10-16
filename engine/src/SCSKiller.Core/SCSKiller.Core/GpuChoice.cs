namespace SCSKiller.Core;

public sealed record GpuChoice(long AdapterLuid, GpuVendor Vendor, uint DeviceId, string Name);
