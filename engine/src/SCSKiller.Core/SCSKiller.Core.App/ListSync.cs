using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SCSKiller.Core.App;

public static class ListSync
{
	public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> want) where T : class
	{
		HashSet<object> hashSet = want.ToHashSet(ReferenceEqualityComparer.Instance);
		for (int num = target.Count - 1; num >= 0; num--)
		{
			if (!hashSet.Contains(target[num]))
			{
				target.RemoveAt(num);
			}
		}
		for (int i = 0; i < want.Count; i++)
		{
			if (i < target.Count && target[i] == want[i])
			{
				continue;
			}
			if (i + 1 < target.Count && target[i + 1] == want[i] && IndexOf(want, target[i], i + 2) >= 0)
			{
				target.RemoveAt(i);
				continue;
			}
			int num2 = IndexOf(target, want[i], i + 1);
			if (num2 >= 0)
			{
				target.RemoveAt(num2);
			}
			target.Insert(i, want[i]);
		}
	}

	private static int IndexOf<T>(IReadOnlyList<T> list, T item, int from) where T : class
	{
		for (int i = from; i < list.Count; i++)
		{
			if (list[i] == item)
			{
				return i;
			}
		}
		return -1;
	}
}
