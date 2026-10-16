using System.Threading.Tasks;

namespace SCSKiller.Core;

public interface IWarmRun
{
	Task<WarmResult> Completion { get; }

	void Pause();

	void Resume();

	void Stop();
}
