using System.Collections.Generic;
using System.IO;

namespace SCSKiller.Core;

public interface IAppCache
{
	IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys);

	long SizeOf(IEnumerable<string> keys);

	IReadOnlySet<string> KeysOpenBy(string exeFileName);

	IReadOnlySet<string> AllKeys();

	int Delete(IEnumerable<string> keys);
}
