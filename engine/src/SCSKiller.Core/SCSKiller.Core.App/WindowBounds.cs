namespace SCSKiller.Core.App;

public sealed record WindowBounds(int Left, int Top, int Width, int Height, bool Maximized)
{
	public static bool WasMaximized(int showCmd, int flags)
	{
		return showCmd switch
		{
			2 => (flags & 2) != 0, 
			3 => true, 
			_ => false, 
		};
	}
}
