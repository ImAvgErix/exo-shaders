using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public sealed record OutcomeReport(string ContentHash, string Vendor, string? GpuModel, string? DriverVersion, string AppVersion, string? RsRule, int PlannerVersion, long Cold = 0L, string[]? Keys = null)
{
	public const int MaxKeys = 100000;

	public const int OldMaxKeys = 2000;

	public static OutcomeReport? Build(string csvPath, string? exeFileName, DateTimeOffset after, PlayWindow? played, IReadOnlySet<string>? rayQuery, OutcomeReport about, long minCreates = 1L)
	{
		List<SessionLog.CsvLaunch> list = SessionLog.Launches(csvPath, 0L).ToList();
		for (int i = 0; i < list.Count; i++)
		{
			SessionLog.CsvLaunch csvLaunch = list[i];
			long? start = csvLaunch.Start;
			if (!start.HasValue)
			{
				continue;
			}
			long valueOrDefault = start.GetValueOrDefault();
			if (valueOrDefault <= after.ToUnixTimeMilliseconds() || SessionLog.OtherExe(csvLaunch.Exe, exeFileName))
			{
				continue;
			}
			int num;
			if ((object)played != null)
			{
				if (played.From.ToUnixTimeMilliseconds() <= valueOrDefault)
				{
					num = ((valueOrDefault <= played.To.ToUnixTimeMilliseconds()) ? 1 : 0);
					goto IL_0089;
				}
			}
			num = 0;
			goto IL_0089;
			IL_0089:
			bool flag = (byte)num != 0;
			if (i == list.Count - 1 && !csvLaunch.End.HasValue && !flag)
			{
				return null;
			}
			if (csvLaunch.Creates.Count(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) =>
			{
				char item4 = c.Kind;
				return (item4 == 'C' || item4 == 'G' || item4 == 'S') ? true : false;
			}) < minCreates)
			{
				continue;
			}
			Dictionary<string, double> dictionary = new Dictionary<string, double>();
			long num2 = 0L;
			foreach (var create in csvLaunch.Creates)
			{
				char item = create.Kind;
				double item2 = create.Ms;
				string item3 = create.Key;
				bool flag2 = ((item == 'A' || item == 'R') ? true : false);
				if (!flag2 && SessionLog.IsCompile(item, item2, item3, rayQuery))
				{
					num2++;
					if (item3 != null && item3.Length == 40 && item3.All(char.IsAsciiHexDigit))
					{
						dictionary[item3.ToLowerInvariant()] = Math.Max(item2, dictionary.GetValueOrDefault(item3.ToLowerInvariant()));
					}
				}
			}
			List<byte>[] array = new List<byte>[4]
			{
				new List<byte>(),
				new List<byte>(),
				new List<byte>(),
				new List<byte>()
			};
			foreach (var (s, num4) in dictionary.OrderByDescending((KeyValuePair<string, double> k) => k.Value).ThenBy((KeyValuePair<string, double> k) => k.Key, StringComparer.Ordinal).Take(100000))
			{
				array[(!(num4 < 10.0)) ? ((num4 < 50.0) ? 1 : ((num4 < 200.0) ? 2 : 3)) : 0].AddRange(Convert.FromHexString(s));
			}
			return about with
			{
				Cold = num2,
				Keys = array.Select((List<byte> b) => Base64Url.EncodeToString(new ReadOnlySpan<byte>(b.ToArray()))).ToArray()
			};
		}
		return null;
	}

	public OutcomeReport Slowest(int max)
	{
		string[] array = new string[4];
		for (int num = 3; num >= 0; num--)
		{
			byte[] array2 = Base64Url.DecodeFromChars(Keys[num]);
			int num2 = Math.Min(array2.Length / 20, max);
			array[num] = Base64Url.EncodeToString(array2.AsSpan(0, num2 * 20));
			max -= num2;
		}
		return this with
		{
			Keys = array
		};
	}
}
