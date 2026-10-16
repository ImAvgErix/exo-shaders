using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

public static class SessionLog
{
	private sealed class Session(CsvLaunch launch)
	{
		public bool OtherExe;

		public long Hits;

		public long Compiles;

		public long StartupCompiles;

		public long Library;

		public long RayQuery;

		public long SoReady;

		public long SoCompiled;

		public long SoStartupCompiled;

		public long PlayHits;

		public long PlaySoReady;

		public double Worst;

		public CsvLaunch Launch => launch;

		public long? Start => launch.Start;

		public long? End => launch.End;

		public List<(double T, char Kind, double Ms, string? Key, long? Tid, bool Presents)> Creates => launch.Creates;

		public double LastT
		{
			get
			{
				if (Creates.Count <= 0)
				{
					return double.NegativeInfinity;
				}
				List<(double T, char Kind, double Ms, string? Key, long? Tid, bool Presents)> creates = Creates;
				return creates[creates.Count - 1].T;
			}
		}

		public Session Count(IReadOnlySet<string>? rayQuery, double startup = double.NegativeInfinity, IReadOnlySet<long>? loads = null)
		{
			Hits = (Compiles = (StartupCompiles = (Library = (RayQuery = (SoReady = (SoCompiled = (SoStartupCompiled = (PlayHits = (PlaySoReady = 0L)))))))));
			Worst = 0.0;
			foreach (var (t, c, num, text, _, _) in Creates)
			{
				if (!char.IsUpper(c))
				{
					Library++;
					continue;
				}
				bool flag = !Started(t);
				if ((c == 'A' || c == 'R') ? true : false)
				{
					if (num < 25.0)
					{
						SoReady++;
						if (flag)
						{
							PlaySoReady++;
						}
					}
					else if (Started(t))
					{
						SoStartupCompiled++;
					}
					else
					{
						SoCompiled++;
					}
					continue;
				}
				if (num > 3.0 && num <= 60.0 && text != null && rayQuery != null && rayQuery.Contains(text))
				{
					RayQuery++;
					if (flag)
					{
						PlayHits++;
					}
					continue;
				}
				if (num <= 3.0)
				{
					Hits++;
					if (flag)
					{
						PlayHits++;
					}
				}
				else if (Started(t))
				{
					StartupCompiles++;
				}
				else
				{
					Compiles++;
				}
				if (!Started(t))
				{
					Worst = Math.Max(Worst, num);
				}
			}
			return this;
			bool Started(double end)
			{
				if (!FrameLog.InStartup(end, startup))
				{
					if (loads != null)
					{
						return FrameLog.InLoad(end, loads);
					}
					return false;
				}
				return true;
			}
		}

		public SessionStats Stats(PlayWindow? played, IReadOnlySet<string>? rayQuery, FrameReport? frames)
		{
			double lastFrame = frames?.Duration.TotalMilliseconds ?? 0.0;
			double num = FrameLog.SessionEnd(launch, lastFrame, played);
			Count(rayQuery, FrameLog.StartupEnd(Creates.Select(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) => (T: c.T, Kind: c.Kind, Ms: c.Ms, Key: c.Key)), rayQuery, num), FrameLog.LoadSeconds(Creates.Select(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) => (T: c.T, Tid: c.Tid, Presents: c.Presents))));
			return new SessionStats(TimeSpan.FromMilliseconds(num), Creates.Count, Library, Hits, Compiles, Worst, RayQuery, SoReady, SoCompiled, StartupCompiles, SoStartupCompiled);
		}

		public bool Exited(PlayWindow? played)
		{
			long? start = Start;
			if (start.HasValue)
			{
				long valueOrDefault = start.GetValueOrDefault();
				if ((object)played != null)
				{
					if (played.From.ToUnixTimeMilliseconds() <= valueOrDefault)
					{
						return valueOrDefault <= played.To.ToUnixTimeMilliseconds();
					}
				}
			}
			return false;
		}
	}

	internal sealed class CsvLaunch
	{
		public long? Start;

		public long? End;

		public double? StartT;

		public double? EndT;

		public string Exe = "";

		public AgsRegistration? Ags;

		public string? SwapChain;

		public bool Overlay;

		public bool FrameGen;

		public bool FramesStopped;

		public bool D3D11;

		public readonly List<(double T, char Kind, double Ms, string? Key, long? Tid, bool Presents)> Creates = new List<(double, char, double, string, long?, bool)>();
	}

	private const double CompileMs = 3.0;

	public const double RayQueryFloorMs = 60.0;

	public const double StateObjectCompileMs = 25.0;

	internal static bool IsCompile(char kind, double ms, string? key, IReadOnlySet<string>? rayQuery)
	{
		bool flag = char.IsUpper(kind);
		if (flag)
		{
			bool flag2 = ((kind == 'A' || kind == 'R') ? true : false);
			flag = (flag2 ? (ms >= 25.0) : (ms > 3.0 && (!(ms <= 60.0) || key == null || !(rayQuery?.Contains(key) ?? false))));
		}
		return flag;
	}

	internal static CreateStats CreatesOf(CsvLaunch l, IReadOnlySet<string>? rayQuery, double startup, double until = double.PositiveInfinity)
	{
		if (until < double.PositiveInfinity)
		{
			CsvLaunch csvLaunch = new CsvLaunch();
			csvLaunch.Creates.AddRange(l.Creates.Where(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) => c.T < until));
			l = csvLaunch;
		}
		Session session = new Session(l).Count(rayQuery, startup, FrameLog.LoadSeconds(l.Creates.Select(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) => (T: c.T, Tid: c.Tid, Presents: c.Presents))));
		return new CreateStats(session.PlayHits, session.Compiles, session.StartupCompiles, session.Library, session.PlaySoReady, session.SoCompiled);
	}

	public static (SessionStats? Last, LaunchedExe? Exe, LaunchCheck? First) Read(string csvPath, string? exeFileName = null, DateTimeOffset? firstAfter = null, long minCreates = 1L, IReadOnlySet<string>? rayQuery = null, PlayWindow? played = null, FrameReport? frames = null)
	{
		bool framesMatch;
		return Read(csvPath, out framesMatch, exeFileName, firstAfter, minCreates, rayQuery, played, frames);
	}

	public static (SessionStats? Last, LaunchedExe? Exe, LaunchCheck? First) Read(string csvPath, out bool framesMatch, string? exeFileName = null, DateTimeOffset? firstAfter = null, long minCreates = 1L, IReadOnlySet<string>? rayQuery = null, PlayWindow? played = null, FrameReport? frames = null)
	{
		AgsRegistration ags;
		return Read(csvPath, out framesMatch, out ags, exeFileName, firstAfter, minCreates, rayQuery, played, frames);
	}

	public static (SessionStats? Last, LaunchedExe? Exe, LaunchCheck? First) Read(string csvPath, out bool framesMatch, out AgsRegistration? ags, string? exeFileName = null, DateTimeOffset? firstAfter = null, long minCreates = 1L, IReadOnlySet<string>? rayQuery = null, PlayWindow? played = null, FrameReport? frames = null)
	{
		framesMatch = false;
		ags = null;
		if (!File.Exists(csvPath))
		{
			return (Last: null, Exe: null, First: null);
		}
		Session session = null;
		LaunchedExe item = null;
		LaunchCheck first = null;
		Session session2 = null;
		List<CsvLaunch> list = Launches(csvPath, 0L).ToList();
		foreach (CsvLaunch item2 in list)
		{
			if (session != null)
			{
				Ended(session);
			}
			bool flag = item2.Start.HasValue && OtherExe(item2.Exe, exeFileName);
			long? start = item2.Start;
			if (start.HasValue)
			{
				long valueOrDefault = start.GetValueOrDefault();
				if (valueOrDefault > 0 && item2.Exe.Length > 0 && !flag)
				{
					item = new LaunchedExe(item2.Exe, DateTimeOffset.FromUnixTimeMilliseconds(valueOrDefault));
				}
			}
			session = new Session(item2)
			{
				OtherExe = flag
			};
			if (!flag)
			{
				session2 = session;
			}
			if (!flag && item2.Ags != null)
			{
				ags = item2.Ags;
			}
		}
		if (session != null && (session.End.HasValue || session.Exited(played)))
		{
			Ended(session);
		}
		framesMatch = session2 != null && frames != null && FrameLog.OwnerOf(list, frames.LaunchUnixMs) == session2.Launch;
		return (Last: session2?.Stats(played, rayQuery, framesMatch ? frames : null), Exe: item, First: first);
		void Ended(Session s)
		{
			if (first == null && firstAfter.HasValue)
			{
				DateTimeOffset valueOrDefault2 = firstAfter.GetValueOrDefault();
				if (s != null)
				{
					long? start2 = s.Start;
					if (start2.HasValue)
					{
						long valueOrDefault3 = start2.GetValueOrDefault();
						if (!s.OtherExe && valueOrDefault3 > valueOrDefault2.ToUnixTimeMilliseconds())
						{
							Session session3 = s.Count(rayQuery);
							if (session3.Hits + session3.Compiles >= minCreates)
							{
								first = new LaunchCheck(DateTimeOffset.FromUnixTimeMilliseconds(valueOrDefault3), session3.Hits, session3.Compiles);
							}
						}
					}
				}
			}
		}
	}

	internal static bool OtherExe(string named, string? exeFileName)
	{
		if (exeFileName != null && exeFileName.All(char.IsAscii))
		{
			return !named.Equals(exeFileName, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	internal static IEnumerable<CsvLaunch> Launches(string path, long from = 0L)
	{
		if (!File.Exists(path))
		{
			yield break;
		}
		FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		fileStream.Position = Math.Min(from, fileStream.Length);
		using (StreamReader reader = new StreamReader(fileStream))
		{
			CsvLaunch csvLaunch = null;
			while (true)
			{
				string text = reader.ReadLine();
				if (text == null)
				{
					break;
				}
				string[] f = text.Split(',');
				if (text.StartsWith('#'))
				{
					long ms = ((f.Length > 1 && long.TryParse(f[1], CultureInfo.InvariantCulture, out var result)) ? result : 0);
					if (f[0] == "#session")
					{
						if (csvLaunch != null)
						{
							yield return Sorted(csvLaunch);
						}
						csvLaunch = new CsvLaunch
						{
							Start = ms,
							Exe = ((f.Length > 2) ? string.Join(',', f[2..]).Trim() : "")
						};
						continue;
					}
					if (f[0] == "#clock" && csvLaunch != null)
					{
						long? start = csvLaunch.Start;
						if (start.HasValue)
						{
							List<(double, char, double, string, long?, bool)> creates = csvLaunch.Creates;
							if (creates != null && creates.Count == 0 && double.TryParse((f.Length > 1) ? f[1] : "", CultureInfo.InvariantCulture, out var result2))
							{
								csvLaunch.StartT = result2;
								continue;
							}
						}
					}
					if (f[0] == "#ags" && csvLaunch != null)
					{
						AgsRegistration agsRegistration = AgsLine(f);
						if ((object)agsRegistration != null)
						{
							csvLaunch.Ags = agsRegistration;
							continue;
						}
					}
					if (f[0] == "#end" && csvLaunch != null)
					{
						csvLaunch.End = ms;
						if (f.Length > 2 && double.TryParse(f[2], CultureInfo.InvariantCulture, out var result3))
						{
							csvLaunch.EndT = result3;
						}
						continue;
					}
					if (f[0] == "#swapchain" && csvLaunch != null)
					{
						long? start = csvLaunch.End;
						if (!start.HasValue && f.Length > 3 && int.TryParse(f[1], CultureInfo.InvariantCulture, out var result4) && int.TryParse(f[2], CultureInfo.InvariantCulture, out var result5) && int.TryParse(f[3], CultureInfo.InvariantCulture, out var result6))
						{
							csvLaunch.SwapChain = $"{result4}x{result5} fmt {result6}";
							continue;
						}
					}
					if (f[0] == "#overlay" && csvLaunch != null)
					{
						long? start = csvLaunch.End;
						if (!start.HasValue)
						{
							csvLaunch.Overlay = true;
							continue;
						}
					}
					if (f[0] == "#framegen" && csvLaunch != null)
					{
						long? start = csvLaunch.End;
						if (!start.HasValue)
						{
							csvLaunch.FrameGen = true;
							continue;
						}
					}
					if (f[0] == "#frames" && csvLaunch != null)
					{
						long? start = csvLaunch.End;
						if (!start.HasValue && f.Length > 1 && f[1] == "stopped")
						{
							csvLaunch.FramesStopped = true;
							continue;
						}
					}
					if (f[0] == "#api" && csvLaunch != null)
					{
						long? start = csvLaunch.End;
						if (!start.HasValue && f.Length > 1 && f[1] == "d3d11")
						{
							csvLaunch.D3D11 = true;
						}
					}
					continue;
				}
				if (f.Length < 5 || f[1].Length != 1 || !double.TryParse(f[0], CultureInfo.InvariantCulture, out var t) || !double.TryParse(f[4], CultureInfo.InvariantCulture, out var ms2))
				{
					continue;
				}
				if (csvLaunch == null || csvLaunch.End.HasValue)
				{
					goto IL_0508;
				}
				if (!csvLaunch.Start.HasValue && csvLaunch.Creates.Count > 0)
				{
					double num = t;
					List<(double T, char Kind, double Ms, string? Key, long? Tid, bool Presents)> creates2 = csvLaunch.Creates;
					if (num < creates2[creates2.Count - 1].T)
					{
						goto IL_0508;
					}
				}
				goto IL_0533;
				IL_0508:
				if (csvLaunch != null)
				{
					yield return Sorted(csvLaunch);
				}
				csvLaunch = new CsvLaunch();
				goto IL_0533;
				IL_0533:
				csvLaunch.Creates.Add((t, f[1][0], ms2, (f.Length > 5) ? f[5] : null, (f.Length > 8 && long.TryParse(f[7], CultureInfo.InvariantCulture, out var result7)) ? new long?(result7) : ((long?)null), f.Length > 8 && f[8] == "1"));
			}
			if (csvLaunch != null)
			{
				yield return Sorted(csvLaunch);
			}
		}
		static CsvLaunch Sorted(CsvLaunch l)
		{
			l.Creates.Sort(((double T, char Kind, double Ms, string? Key, long? Tid, bool Presents) a, (double T, char Kind, double Ms, string? Key, long? Tid, bool Presents) b) => a.T.CompareTo(b.T));
			return l;
		}
	}

	public static AgsRegistration? LastAgs(string csvPath, string? exeFileName)
	{
		return Launches(csvPath, 0L).LastOrDefault((CsvLaunch l) => l.Ags != null && (!l.Start.HasValue || !OtherExe(l.Exe, exeFileName)))?.Ags;
	}

	internal static AgsRegistration? AgsLine(string[] f)
	{
		if (f.Length == 6)
		{
			string text = Unescape(f[4]);
			if (text != null && text.Length > 0)
			{
				string text2 = Unescape(f[5]);
				if (text2 != null)
				{
					return new AgsRegistration(text, text2);
				}
			}
		}
		return null;
		static string? Unescape(string s)
		{
			StringBuilder stringBuilder = new StringBuilder(s.Length);
			for (int i = 0; i < s.Length; i++)
			{
				if (s[i] != '%')
				{
					stringBuilder.Append(s[i]);
				}
				else
				{
					if (i + 4 >= s.Length || !ushort.TryParse(s.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result))
					{
						return null;
					}
					stringBuilder.Append((char)result);
					i += 4;
				}
			}
			return stringBuilder.ToString();
		}
	}

	internal static long LastSessionAt(string path)
	{
		ReadOnlySpan<byte> value = "\n#session,"u8;
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			byte[] array = new byte[65536];
			long num = fileStream.Length;
			while (num > 0)
			{
				long num2 = (fileStream.Position = Math.Max(0L, num - array.Length));
				fileStream.ReadExactly(array, 0, (int)(num - num2));
				int num4 = ((ReadOnlySpan<byte>)array.AsSpan(0, (int)(num - num2))).LastIndexOf(value);
				if (num4 >= 0)
				{
					return num2 + num4 + 1;
				}
				if (num2 == 0L)
				{
					return 0L;
				}
				num = num2 + value.Length - 1;
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return 0L;
	}

	public static void WriteRayQueryKeys(IEnumerable<string> dbs, string keysFile)
	{
		HashSet<string> rayQuery = new HashSet<string>();
		List<(string, ICollection<string>)> list = new List<(string, ICollection<string>)>();
		foreach (PsoDb.Rec item in dbs.Where(File.Exists).SelectMany(PsoDb.Read))
		{
			if (item.Tag == 'B')
			{
				if (item.Payload.Length > 20 && Dxbc.InlineRayTracing(item.Payload.AsSpan(20)))
				{
					rayQuery.Add(PsoDb.Hex(item.Payload.AsSpan(0, 20)));
				}
				continue;
			}
			char tag = item.Tag;
			if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
			{
				try
				{
					list.Add((item.Key, PsoDb.Parse(item).Stages.Values));
				}
				catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException) ? true : false)
				{
				}
			}
		}
		File.WriteAllLines(keysFile, list.Where(((string Key, ICollection<string> Stages) p) => p.Stages.Any(rayQuery.Contains)).Select(((string Key, ICollection<string> Stages) p) => p.Key));
	}

	public static IReadOnlySet<string>? ReadRayQueryKeys(string keysFile)
	{
		if (!File.Exists(keysFile))
		{
			return null;
		}
		return File.ReadAllLines(keysFile).ToHashSet();
	}
}
