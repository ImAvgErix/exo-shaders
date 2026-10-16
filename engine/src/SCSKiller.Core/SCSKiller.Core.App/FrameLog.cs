using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

public static class FrameLog
{
	private sealed record Launch(long UnixMs, List<double> Ends, List<double>? Displayed = null);

	private sealed record Create(double End, double Ms, char Kind, string Key, long? Tid, bool Presents);

	internal sealed record LaunchContext(string? SwapChain, bool Overlay, bool Ended, bool D3D11, bool FrameGen);

	public const string FileName = "scskiller_frames.bin";

	public const double HitchMs = 50.0;

	public const int GraphColumns = 1200;

	private const double BlockMs = 10.0;

	private const int LoadCreates = 100;

	private const int StartupQuietCreates = 30;

	private const double PauseMs = 5000.0;

	public const double QuitMs = 10000.0;

	private const int BurstCreates = 100;

	private const double BurstGapMs = 10000.0;

	private const double ColdCompileMs = 100.0;

	private const int LoadThreads = 8;

	private static readonly ConditionalWeakTable<FrameReport, LaunchContext> Contexts = new ConditionalWeakTable<FrameReport, LaunchContext>();

	internal const uint TagEnd = 268435u;

	public static FrameReport? Read(string framesPath, string csvPath, string? exeFileName = null, IReadOnlySet<string>? rayQuery = null, PlayWindow? played = null, double? playCapMs = null)
	{
		if (!File.Exists(framesPath))
		{
			return null;
		}
		List<Launch> list = ReadFrames(framesPath);
		List<SessionLog.CsvLaunch> source = (from x in SessionLog.Launches(csvPath, SessionLog.LastSessionAt(csvPath))
			where x.StartT.HasValue
			select x).ToList();
		List<SessionLog.CsvLaunch> list2 = null;
		for (int num = list.Count - 1; num >= 0; num--)
		{
			Launch l = list[num];
			if (l.Ends.Count >= 2)
			{
				SessionLog.CsvLaunch csvLaunch = source.FirstOrDefault((SessionLog.CsvLaunch x) => x.Start == l.UnixMs) ?? OwnerOf(list2 ?? (list2 = (from x in SessionLog.Launches(csvPath, 0L)
					where x.Start.HasValue
					select x).ToList()), l.UnixMs);
				if (csvLaunch == null || !SessionLog.OtherExe(csvLaunch.Exe, exeFileName))
				{
					return Report(l, csvLaunch, rayQuery, played, playCapMs);
				}
			}
		}
		return null;
	}

	internal static double SessionEnd(SessionLog.CsvLaunch? l, double lastFrame, PlayWindow? played)
	{
		double val;
		if (l != null)
		{
			List<(double, char, double, string, long?, bool)> creates = l.Creates;
			if (creates != null && creates.Count > 0)
			{
				List<(double T, char Kind, double Ms, string? Key, long? Tid, bool Presents)> creates2 = l.Creates;
				val = creates2[creates2.Count - 1].T;
				goto IL_003c;
			}
		}
		val = 0.0;
		goto IL_003c;
		IL_003c:
		double num = Math.Max(val, lastFrame);
		long? num2 = l?.Start;
		if (num2.HasValue)
		{
			long valueOrDefault = num2.GetValueOrDefault();
			double valueOrDefault2 = l.StartT.GetValueOrDefault();
			double? endT = l.EndT;
			if (endT.HasValue)
			{
				double valueOrDefault3 = endT.GetValueOrDefault();
				return Math.Max(num, valueOrDefault3);
			}
			num2 = l.End;
			if (num2.HasValue)
			{
				long valueOrDefault4 = num2.GetValueOrDefault();
				if (valueOrDefault4 >= valueOrDefault)
				{
					return Math.Max(num, (double)(valueOrDefault4 - valueOrDefault) + valueOrDefault2);
				}
			}
			if ((object)played != null)
			{
				if (played.From.ToUnixTimeMilliseconds() <= valueOrDefault && valueOrDefault <= played.To.ToUnixTimeMilliseconds())
				{
					num = Math.Max(num, (double)(played.To.ToUnixTimeMilliseconds() - valueOrDefault) + valueOrDefault2);
				}
			}
			return num;
		}
		return num;
	}

	internal static SessionLog.CsvLaunch? OwnerOf(IEnumerable<SessionLog.CsvLaunch> launches, long launchUnix)
	{
		List<SessionLog.CsvLaunch> list = launches.Where((SessionLog.CsvLaunch l) =>
		{
			long? start = l.Start;
			if (start.HasValue)
			{
				long valueOrDefault = start.GetValueOrDefault();
				return Math.Abs(valueOrDefault - launchUnix) < 10000;
			}
			return false;
		}).ToList();
		SessionLog.CsvLaunch csvLaunch = list.FirstOrDefault((SessionLog.CsvLaunch l) => l.StartT.HasValue && l.Start == launchUnix);
		if (csvLaunch == null)
		{
			if (list != null && list.Count == 1)
			{
				SessionLog.CsvLaunch csvLaunch2 = list[0];
				if (csvLaunch2 != null)
				{
					double? startT = csvLaunch2.StartT;
					if (!startT.HasValue)
					{
						csvLaunch = csvLaunch2;
						goto IL_0065;
					}
				}
			}
			return null;
		}
		goto IL_0065;
		IL_0065:
		return csvLaunch;
	}

	private static FrameReport Report(Launch l, SessionLog.CsvLaunch? session, IReadOnlySet<string>? rayQuery, PlayWindow? played, double? playCapMs)
	{
		List<Create> list = session?.Creates.Select(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) => new Create(c.T, c.Ms, c.Kind, c.Key ?? "", c.Tid, c.Presents)).ToList() ?? new List<Create>();
		list.Sort((Create a, Create b) => a.End.CompareTo(b.End));
		List<double> ends = l.Ends;
		double num = SessionEnd(session, ends[ends.Count - 1], played);
		double startup = StartupEnd(list.Select((Create c) => (End: c.End, Kind: c.Kind, Ms: c.Ms, c.Key)), rayQuery, num, out var firstQuiet);
		double num2 = startup;
		double num3 = (from c in list
			where !InStartup(c.End, startup) && SessionLog.IsCompile(c.Kind, c.Ms, c.Key, rayQuery)
			select c.End).DefaultIfEmpty(double.PositiveInfinity).Min();
		List<(double, double)> list2 = l.Ends.Zip(l.Ends.Skip(1), (double a, double b) => (Start: a, Ms: b - a)).ToList();
		List<double> ends2 = l.Ends;
		double quit = ends2[ends2.Count - 1] - 10000.0;
		List<double> v = list.Select((Create c) => c.End).ToList();
		List<(double, double, int, bool, bool, double, bool)> list3 = new List<(double, double, int, bool, bool, double, bool)>();
		foreach (var item13 in list2.Where(((double Start, double Ms) f) => f.Ms >= 50.0))
		{
			double item = item13.Item1;
			double item2 = item13.Item2;
			int num4 = 0;
			bool item3 = false;
			bool flag = false;
			bool flag2 = false;
			double num5 = 0.0;
			HashSet<long> hashSet = new HashSet<long>();
			for (int num6 = LowerBound(v, item); num6 < list.Count && list[num6].End <= item + item2; num6++)
			{
				Create create = list[num6];
				if (create.End <= item)
				{
					continue;
				}
				num4++;
				long? tid = create.Tid;
				if (tid.HasValue)
				{
					long valueOrDefault = tid.GetValueOrDefault();
					hashSet.Add(valueOrDefault);
				}
				flag2 |= create.Presents;
				if (SessionLog.IsCompile(create.Kind, create.Ms, create.Key, rayQuery))
				{
					item3 = true;
					flag |= Blocks(create.Kind, create.Ms);
					if (create.Ms >= 100.0)
					{
						num5 += create.End - Math.Max(create.End - create.Ms, item);
					}
				}
			}
			list3.Add((item, item2, num4, item3, flag, num5, Parallel(hashSet.Count, flag2)));
		}
		foreach (var item14 in list3)
		{
			if (item14.Item3 == 0 && item14.Item1 < num2 && !InStartup(item14.Item1 + item14.Item2, num2) && InStartup(item14.Item1 + item14.Item2, Allowance(firstQuiet)) && item14.Item1 + item14.Item2 < num3)
			{
				startup = Math.Max(startup, item14.Item1 + item14.Item2);
			}
		}
		if (playCapMs.HasValue)
		{
			startup = l.Ends[0];
		}
		List<Hitch> list4 = new List<Hitch>();
		HashSet<double> paused = new HashSet<double>();
		foreach (var item15 in list3)
		{
			double item4 = item15.Item1;
			double item5 = item15.Item2;
			int item6 = item15.Item3;
			bool item7 = item15.Item4;
			bool item8 = item15.Item5;
			double item9 = item15.Item6;
			bool item10 = item15.Item7;
			bool flag3 = item9 >= item5 / 2.0;
			HitchCause hitchCause;
			if (InStartup(item4 + item5, startup))
			{
				hitchCause = (flag3 ? HitchCause.LoadingShaders : HitchCause.Loading);
			}
			else if (item4 >= quit)
			{
				hitchCause = HitchCause.Quitting;
			}
			else
			{
				hitchCause = ((item7 & item10) ? HitchCause.Loading : ((!flag3) ? ((item6 >= 100) ? HitchCause.Loading : ((!item8) ? HitchCause.Other : HitchCause.Shader)) : HitchCause.Shader));
			}
			if (!playCapMs.HasValue && hitchCause == HitchCause.Other && item5 >= 5000.0 && !item7)
			{
				paused.Add(item4);
			}
			else
			{
				list4.Add(new Hitch(TimeSpan.FromMilliseconds(item4), item5, hitchCause));
			}
		}
		List<(double, double)> list5 = list2.Where(((double Start, double Ms) f) => InPlay(f.Start, f.Ms, startup) && f.Start < quit && !paused.Contains(f.Start)).ToList();
		double cut = double.PositiveInfinity;
		double num7 = 0.0;
		if (playCapMs.HasValue)
		{
			double valueOrDefault2 = playCapMs.GetValueOrDefault();
			foreach (var item16 in list5)
			{
				if (num7 >= valueOrDefault2)
				{
					(cut, _) = item16;
					break;
				}
				num7 += item16.Item2;
			}
		}
		if (cut < num)
		{
			num = cut + 10000.0;
			list2 = list2.Where(((double Start, double Ms) f) => f.Start < cut).ToList();
			list4.RemoveAll((Hitch h) => h.At >= TimeSpan.FromMilliseconds(cut));
		}
		List<double> list6 = list5.Where(((double Start, double Ms) f) => f.Start < cut).Select(((double Start, double Ms) f) => f.Ms).OrderDescending()
			.ToList();
		double num8 = list6.Sum() / 1000.0;
		List<double> list7 = list6.Take(Math.Max(1, list6.Count / 100)).ToList();
		double low1PctFps = ((list7.Count > 0) ? ((double)(1000 * list7.Count) / list7.Sum()) : 0.0);
		float[] array = new float[1200];
		foreach (var item17 in list2)
		{
			double item11 = item17.Item1;
			double item12 = item17.Item2;
			int num9 = Math.Clamp((int)(item11 / num * 1200.0), 0, 1199);
			array[num9] = Math.Max(array[num9], (float)item12);
		}
		FrameReport frameReport = new FrameReport(TimeSpan.FromMilliseconds(num), TimeSpan.FromMilliseconds(Math.Min(startup, num)), list2.Count, low1PctFps, list4, array, l.UnixMs, num8, (num8 > 0.0) ? ((double)list6.Count / num8) : 0.0, (session != null) ? SessionLog.CreatesOf(session, rayQuery, Math.Min(startup, num), cut) : null, l.Displayed?.Count ?? 0, l.Displayed);
		if (session != null)
		{
			Contexts.AddOrUpdate(frameReport, new LaunchContext(session.SwapChain, session.Overlay, session.End.HasValue, session.D3D11, session.FrameGen));
		}
		return frameReport;
	}

	internal static LaunchContext? ContextOf(FrameReport r)
	{
		if (!Contexts.TryGetValue(r, out LaunchContext value))
		{
			return null;
		}
		return value;
	}

	internal static bool InStartup(double end, double startup)
	{
		return end <= startup;
	}

	public static bool InPlay(double start, double ms, double startup)
	{
		return !InStartup(start + ms, startup);
	}

	private static bool Parallel(int threads, bool presenter)
	{
		if (threads >= 8)
		{
			return !presenter;
		}
		return false;
	}

	internal static HashSet<long> LoadSeconds(IEnumerable<(double End, long? Tid, bool Presents)> creates)
	{
		Dictionary<long, (int, bool)> source = (from c in creates
			group c by (long)(c.End / 1000.0)).ToDictionary((IGrouping<long, (double End, long? Tid, bool Presents)> g) => g.Key, (IGrouping<long, (double End, long? Tid, bool Presents)> g) => (N: g.Count(), Parallel: Parallel(g.Select(((double End, long? Tid, bool Presents) c) => c.Tid).OfType<long>().Distinct()
			.Count(), g.Any(((double End, long? Tid, bool Presents) c) => c.Presents))));
		HashSet<long> bursts = source.Where((KeyValuePair<long, (int N, bool Parallel)> x) => x.Value.N >= 100 && x.Value.Parallel).Select((KeyValuePair<long, (int N, bool Parallel)> x) => x.Key).ToHashSet();
		return source.Where((KeyValuePair<long, (int N, bool Parallel)> x) => bursts.Contains(x.Key) || (x.Value.Parallel && (bursts.Contains(x.Key - 1) || bursts.Contains(x.Key + 1)))).Select((KeyValuePair<long, (int N, bool Parallel)> x) => x.Key).ToHashSet();
	}

	internal static bool InLoad(double end, IReadOnlySet<long> loads)
	{
		return loads.Contains((long)(end / 1000.0));
	}

	private static bool Blocks(char kind, double ms)
	{
		if ((kind != 'A' && kind != 'R') || 1 == 0)
		{
			return ms >= 10.0;
		}
		return true;
	}

	private static double Allowance(double firstQuiet)
	{
		return firstQuiet + 10000.0;
	}

	internal static double StartupEnd(IEnumerable<(double End, char Kind, double Ms, string? Key)> creates, IReadOnlySet<string>? rayQuery, double sessionEnd)
	{
		double firstQuiet;
		return StartupEnd(creates, rayQuery, sessionEnd, out firstQuiet);
	}

	internal static double StartupEnd(IEnumerable<(double End, char Kind, double Ms, string? Key)> creates, IReadOnlySet<string>? rayQuery, double sessionEnd, out double firstQuiet)
	{
		firstQuiet = double.PositiveInfinity;
		Dictionary<long, (int Creates, int Compiles, double First)> perSecond = new Dictionary<long, (int, int, double)>();
		List<double> list = new List<double>();
		foreach (var create in creates)
		{
			double item = create.End;
			char item2 = create.Kind;
			double item3 = create.Ms;
			string item4 = create.Key;
			bool flag = SessionLog.IsCompile(item2, item3, item4, rayQuery);
			if (flag && Blocks(item2, item3))
			{
				list.Add(item);
			}
			var (num, num2, val) = perSecond.GetValueOrDefault((long)(item / 1000.0), (0, 0, double.PositiveInfinity));
			perSecond[(long)(item / 1000.0)] = (num + 1, num2 + (flag ? 1 : 0), Math.Min(val, item));
		}
		if (perSecond.Count == 0)
		{
			return firstQuiet = 0.0;
		}
		long num3 = perSecond.Keys.Min();
		long num4;
		for (num4 = num3; (double)(num4 - num3) < 10.0 && !Starts(num4); num4++)
		{
		}
		if (!Starts(num4))
		{
			num4 = num3;
		}
		long? num5 = Quiet(num4);
		if (num5.HasValue)
		{
			long first = num5.GetValueOrDefault();
			firstQuiet = (double)first * 1000.0;
			double end = firstQuiet;
			double cap = Allowance(firstQuiet);
			foreach (long item5 in perSecond.Keys.Where((long k) => k > first && perSecond[k].First <= cap && perSecond[k].Creates >= 100).Order())
			{
				double start = perSecond[item5].First;
				if (!InStartup(start, end))
				{
					if (list.Any((double e) => !InStartup(e, end) && e < start))
					{
						break;
					}
					num5 = Quiet(item5);
					double num6;
					if (num5.HasValue)
					{
						long valueOrDefault = num5.GetValueOrDefault();
						num6 = (double)valueOrDefault * 1000.0;
					}
					else
					{
						num6 = cap;
					}
					end = num6;
				}
			}
			return Math.Min(end, cap);
		}
		return double.PositiveInfinity;
		bool Busy(long s)
		{
			List<(int, int, double)> source = new long[3]
			{
				s,
				s + 1,
				s + 2
			}.Select((long k) => perSecond.GetValueOrDefault(k)).ToList();
			if (source.Sum(((int Creates, int Compiles, double First) x) => x.Creates) >= 30)
			{
				if (!source.Any(((int Creates, int Compiles, double First) x) => x.Compiles > 0))
				{
					return source.Any(((int Creates, int Compiles, double First) x) => x.Creates >= 100);
				}
				return true;
			}
			return false;
		}
		long? Quiet(long from)
		{
			for (long num7 = from; num7 == from || (double)(num7 * 1000) < sessionEnd; num7++)
			{
				if (!Busy(num7) && (num7 == from || (double)((num7 + 3) * 1000) <= sessionEnd))
				{
					return num7;
				}
			}
			return null;
		}
		bool Starts(long k)
		{
			if (perSecond.GetValueOrDefault(k).Creates >= 30)
			{
				return Busy(k);
			}
			return false;
		}
	}

	private static int LowerBound(List<double> v, double x)
	{
		int num = v.BinarySearch(x);
		if (num < 0)
		{
			return ~num;
		}
		while (num > 0 && v[num - 1] >= x)
		{
			num--;
		}
		return num;
	}

	private static List<Launch> ReadFrames(string path)
	{
		List<Launch> launches = new List<Launch>();
		byte[] array;
		using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			array = new byte[fileStream.Length];
			fileStream.ReadExactly(array);
		}
		long unix = 0L;
		long num = 0L;
		double num2 = 0.0;
		Dictionary<uint, List<double>> chains = null;
		HashSet<uint> games = new HashSet<uint>();
		for (int i = 0; i + 4 <= array.Length; i += 4)
		{
			uint num3 = BitConverter.ToUInt32(array, i);
			if (num3 == uint.MaxValue)
			{
				if (i + 36 > array.Length)
				{
					break;
				}
				Close();
				unix = BitConverter.ToInt64(array, i + 4);
				num2 = (double)BitConverter.ToInt64(array, i + 12) / 1000.0;
				Dictionary<uint, List<double>> dictionary = new Dictionary<uint, List<double>>();
				HashSet<uint> hashSet = new HashSet<uint>();
				games = hashSet;
				chains = dictionary;
				num = 0L;
				i += 32;
			}
			else
			{
				if (chains == null)
				{
					continue;
				}
				if (num3 >> 28 == 15 && (num3 & 0xFFFFFFF) < 268435)
				{
					if ((num3 & 0xFFFFFFF) < 15)
					{
						games.Add(num3 & 0xFFFFFFF);
					}
					continue;
				}
				if (num3 >> 28 == 15)
				{
					num += (long)(num3 & 0xFFFFFFF) * 1000L;
					continue;
				}
				num += num3 & 0xFFFFFFF;
				if (!chains.TryGetValue(num3 >> 28, out List<double> value))
				{
					value = (chains[num3 >> 28] = new List<double>());
				}
				value.Add(num2 + (double)num / 1000.0);
			}
		}
		Close();
		return launches;
		void Close()
		{
			if (chains != null)
			{
				List<double> own = chains.Where((KeyValuePair<uint, List<double>> c) => games.Contains(c.Key)).SelectMany((KeyValuePair<uint, List<double>> c) => c.Value).Order()
					.ToList();
				if (own.Count == 0)
				{
					launches.Add(new Launch(unix, chains.Values.MaxBy((List<double> c) => c.Count) ?? new List<double>()));
				}
				else
				{
					List<List<double>> source = (from c in chains
						where !games.Contains(c.Key)
						select c.Value).ToList();
					List<double> list2 = source.Select((List<double> c) => c.Where((double t) => t < own[0]).ToList()).MaxBy((List<double> c) => c.Count) ?? new List<double>();
					List<Launch> list3 = launches;
					long unixMs = unix;
					List<double> list4 = own;
					int num4 = list2.Count + list4.Count;
					List<double> list5 = new List<double>(num4);
					CollectionsMarshal.SetCount(list5, num4);
					Span<double> span = CollectionsMarshal.AsSpan(list5);
					int num5 = 0;
					Span<double> span2 = CollectionsMarshal.AsSpan(list2);
					span2.CopyTo(span.Slice(num5, span2.Length));
					num5 += span2.Length;
					Span<double> span3 = CollectionsMarshal.AsSpan(list4);
					span3.CopyTo(span.Slice(num5, span3.Length));
					num5 += span3.Length;
					list3.Add(new Launch(unixMs, list5, source.SelectMany((List<double> c) => c).Order().ToList()));
				}
			}
		}
	}
}
