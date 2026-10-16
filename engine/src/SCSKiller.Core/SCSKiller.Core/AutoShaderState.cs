namespace SCSKiller.Core;

public sealed record AutoShaderState(AutoShaderCompilation Level, bool TaskReady, bool TaskEnabled)
{
	public bool Is(AutoShaderCompilation level)
	{
		if (Level == level)
		{
			if (level != AutoShaderCompilation.Off)
			{
				return TaskReady;
			}
			return !TaskEnabled;
		}
		return false;
	}
}
