namespace SCSKiller.Core.App;

public static class WindowClose
{
	public static CloseAction Of(Settings s, bool quitting, bool trayAdded)
	{
		if (!s.CloseQuits || quitting)
		{
			if (!(trayAdded | quitting))
			{
				return CloseAction.Close;
			}
			return CloseAction.Hide;
		}
		return CloseAction.Quit;
	}
}
