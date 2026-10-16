using System;

internal sealed class StderrLog : IProgress<string>
{
	public void Report(string value)
	{
		Console.Error.WriteLine("  " + value);
	}
}
