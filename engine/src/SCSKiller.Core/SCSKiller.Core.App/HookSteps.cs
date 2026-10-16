using System;
using System.Collections.Generic;

namespace SCSKiller.Core.App;

public static class HookSteps
{
	public static void Run(string hook, Action<string> log, params Action[] steps)
	{
		log(hook);
		for (int i = 0; i < steps.Length; i++)
		{
			try
			{
				steps[i]();
			}
			catch (Exception value)
			{
				log($"{hook}: step {i + 1} of {steps.Length} failed, the others ran: {value}");
			}
		}
	}

	public static void Uninstall(AppStore store, Action<string> log, Action unregisterTask, Action removeStartup, IReadOnlySet<string>? running = null)
	{
		Run("Uninstalling", log, unregisterTask, removeStartup, () =>
		{
			ScsKiller.RemoveAllRecorders(store, running);
		});
	}
}
