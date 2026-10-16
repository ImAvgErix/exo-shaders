using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.Warming;

public sealed class RecentRate
{
	private static readonly TimeSpan Window = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(5L);

	private readonly Queue<(TimeSpan At, long Done)> _samples = new Queue<(TimeSpan, long)>();

	public double? PerSecond
	{
		get
		{
			if (_samples.Count < 2)
			{
				return null;
			}
			var (timeSpan, num) = _samples.Peek();
			var (timeSpan2, num2) = _samples.Last();
			if (!(timeSpan2 - timeSpan >= MinSpan))
			{
				return null;
			}
			return (double)Math.Max(0L, num2 - num) / (timeSpan2 - timeSpan).TotalSeconds;
		}
	}

	public void Add(TimeSpan at, long done)
	{
		_samples.Enqueue((at, done));
		while (_samples.Count > 2 && at - _samples.Peek().At > Window)
		{
			_samples.Dequeue();
		}
	}
}
