using System;
using System.Collections.Generic;

namespace SCSKiller.Core.App;

public sealed class BeforeAfterRecord
{
	public BeforeAfterStage Stage { get; set; }

	public int Minutes { get; set; } = 5;

	public DateTimeOffset ArmedAt { get; set; }

	public string? DriverId { get; set; }

	public string? Build { get; set; }

	public bool Dirty { get; set; }

	public long? Before { get; set; }

	public long? After { get; set; }

	public string? Problem { get; set; }

	public bool ClearPending { get; set; }

	public bool Overlay { get; set; } = true;

	public HashSet<string>? Survivors { get; set; }

	public bool? ClearGap { get; set; }

	public bool DxShared { get; set; }
}
