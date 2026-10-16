using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public static class NewShaders
{
	private readonly record struct Told(long Warm, long Count, long At, bool Planner, int Auto = 0)
	{
		public static Told? Parse(string? entry, DateTimeOffset now)
		{
			string[] array = entry?.Split('|');
			Told value;
			if (array != null && array.Length >= 2)
			{
				string s = array[0];
				string s2 = array[1];
				string[] subArray = array[new Index(2)..^0];
				if (long.TryParse(s, out var result) && long.TryParse(s2, out var result2))
				{
					if (subArray != null && subArray.Length >= 1)
					{
						string s3 = subArray[0];
						if (long.TryParse(s3, out var result3))
						{
							long warm = result;
							long count = result2;
							long at = result3;
							int planner;
							if (subArray != null && subArray.Length == 2)
							{
								string text = subArray[1];
								planner = (text.Contains('p') ? 1 : 0);
							}
							else
							{
								planner = 0;
							}
							int auto;
							if (subArray != null && subArray.Length == 2)
							{
								string text2 = subArray[1];
								auto = (text2.Contains('A') ? 2 : (text2.Contains('a') ? 1 : 0));
							}
							else
							{
								auto = 0;
							}
							value = new Told(warm, count, at, (byte)planner != 0, auto);
							goto IL_00dd;
						}
					}
					value = new Told(result, result2, now.UtcTicks, Planner: true);
					goto IL_00dd;
				}
			}
			return null;
			IL_00dd:
			return value;
		}

		public override string ToString()
		{
			string text = $"{Warm}|{Count}|{At}";
			string text3;
			if (Planner || Auto > 0)
			{
				string text2 = (Planner ? "p" : "");
				text3 = "|" + text2 + Auto switch
				{
					1 => "a", 
					2 => "A", 
					_ => "", 
				};
			}
			else
			{
				text3 = "";
			}
			return text + text3;
		}
	}

	public const long MinNew = 500L;

	public static long Count(GameState s)
	{
		return s.NewPipelines.GetValueOrDefault() + s.RecordedSinceWarm;
	}

	public static long Minimum(PlanStats? p)
	{
		return Math.Max((((object)p == null) ? 0 : (p.Recorded + p.Generated + p.D3D11Shaders + p.MiddlewareItems)) / 100, 500L);
	}

	private static bool PlannerChanged(GameState s)
	{
		if (s.Status == GameStatus.Stale && s.StatusReason.StartsWith("SCSKiller can now compile ", StringComparison.Ordinal))
		{
			return !s.StatusReason.StartsWith("SCSKiller can now compile more", StringComparison.Ordinal);
		}
		return false;
	}

	public static string? Key(GameState s, IReadOnlySet<string> driverStale)
	{
		DateTimeOffset valueOrDefault = default;
		bool flag;
		if ((object)s != null)
		{
			GameStatus status = s.Status;
			if ((uint)(status - 3) <= 1u && s.AntiCheat == AntiCheat.None)
			{
				DateTimeOffset? warmedAt = s.WarmedAt;
				if (warmedAt.HasValue)
				{
					valueOrDefault = warmedAt.GetValueOrDefault();
					if (s.NoStutter == null && !s.CompileUnreached)
					{
						flag = true;
						goto IL_0048;
					}
				}
			}
		}
		flag = false;
		goto IL_0048;
		IL_0048:
		if (flag && !driverStale.Contains(s.Game.Id))
		{
			long num = Count(s);
			if (num > 0)
			{
				return $"{valueOrDefault.UtcTicks}|{num}";
			}
		}
		return null;
	}

	public static NewShadersMode Global(Settings s)
	{
		NewShadersMode? onNewShaders = s.OnNewShaders;
		if (!onNewShaders.HasValue)
		{
			if (!s.NotifyNewShaders)
			{
				return NewShadersMode.Off;
			}
			return NewShadersMode.Notify;
		}
		return onNewShaders.GetValueOrDefault();
	}

	public static NewShadersMode ModeOf(GameState s, Settings settings)
	{
		return s.NewShadersOverride ?? Global(settings);
	}

	public static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) Due(IEnumerable<GameState> games, IEnumerable<QueueItem> queue, IReadOnlyDictionary<string, string> notified, IReadOnlySet<string> driverStale, DateTimeOffset now)
	{
		(IReadOnlyList<GameState>, IReadOnlyList<GameState>, Dictionary<string, string>) tuple = Plan(games, queue, notified, driverStale, now, (GameState _) => NewShadersMode.Notify, compiling: false);
		return (Due: tuple.Item1, Notified: tuple.Item3);
	}

	public static (IReadOnlyList<GameState> Notify, IReadOnlyList<GameState> Compile, Dictionary<string, string> Notified) Plan(IEnumerable<GameState> games, IEnumerable<QueueItem> queue, IReadOnlyDictionary<string, string> notified, IReadOnlySet<string> driverStale, DateTimeOffset now, Func<GameState, NewShadersMode> mode, bool compiling)
	{
		HashSet<string> hashSet = (from q in queue.Where((QueueItem q) =>
			{
				bool flag9 = !q.PlanCheck;
				if (flag9)
				{
					QueueStage stage = q.Stage;
					bool flag10 = (uint)(stage - 6) <= 2u;
					flag9 = !flag10;
				}
				return flag9;
			})
			select q.GameId).ToHashSet();
		List<GameState> list = new List<GameState>();
		List<GameState> list2 = new List<GameState>();
		List<GameState> list3 = list;
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		foreach (GameState game in games)
		{
			Told? told = Told.Parse(notified.GetValueOrDefault(game.Game.Id), now);
			int num;
			if (told.HasValue)
			{
				Told valueOrDefault = told.GetValueOrDefault();
				num = ((now.UtcTicks - valueOrDefault.At < 864000000000L) ? 1 : 0);
			}
			else
			{
				num = 0;
			}
			bool flag = (byte)num != 0;
			long? num2 = game.WarmedAt?.UtcTicks;
			bool flag2 = told.HasValue && told.GetValueOrDefault().Warm == num2;
			NewShadersMode newShadersMode = mode(game);
			if (newShadersMode == NewShadersMode.Off || Key(game, driverStale) == null)
			{
				if (flag)
				{
					dictionary[game.Game.Id] = told.ToString();
				}
				continue;
			}
			long num3 = Count(game);
			int num4;
			if (told.HasValue)
			{
				Told valueOrDefault2 = told.GetValueOrDefault();
				if (valueOrDefault2.Auto > 0 && !flag2 && num3 >= valueOrDefault2.Count)
				{
					num4 = ((num3 - valueOrDefault2.Count < Minimum(game.Plan)) ? 1 : 0);
					goto IL_01be;
				}
			}
			num4 = 0;
			goto IL_01be;
			IL_01be:
			bool flag3 = (byte)num4 != 0;
			int num5 = ((flag2 && num3 - told.Value.Count < Minimum(game.Plan)) ? told.Value.Auto : 0);
			bool flag4 = ScsKiller.IsPartlyCompiled(game with
			{
				Status = GameStatus.Warmed
			});
			bool flag5 = (uint)(newShadersMode - 2) <= 1u;
			bool flag6 = flag5 && !flag3 && !flag4 && num5 < 2;
			bool flag7 = PlannerChanged(game) && (!flag2 || !told.Value.Planner);
			long num6 = ((flag2 && !flag6) ? told.Value.Count : 0);
			bool flag8 = num3 - num6 < Minimum(game.Plan) && !flag7;
			if (!flag7 && (flag || (flag8 && !flag6)))
			{
				if (flag2 | flag)
				{
					dictionary[game.Game.Id] = told.ToString();
				}
				continue;
			}
			if (game.Playing || hashSet.Contains(game.Game.Id) || (flag6 & compiling))
			{
				if (told.HasValue)
				{
					dictionary[game.Game.Id] = told.ToString();
				}
				continue;
			}
			if (flag6)
			{
				list2.Add(game);
			}
			if (!flag8 && (!flag6 || newShadersMode == NewShadersMode.CompileAndNotify))
			{
				list3.Add(game);
			}
			Dictionary<string, string> dictionary2 = dictionary;
			string id = game.Game.Id;
			long value = num2.Value;
			long count = num3;
			long utcTicks = now.UtcTicks;
			bool planner = flag7 || (flag2 && told.Value.Planner);
			int auto;
			if (flag6)
			{
				auto = num5 + 1;
			}
			else
			{
				auto = (flag3 ? 2 : num5);
			}
			dictionary2[id] = new Told(value, count, utcTicks, planner, auto).ToString();
		}
		return (Notify: list3, Compile: list2, Notified: dictionary);
	}

	public static (IReadOnlyList<string> Withdraw, IReadOnlyList<string> Forget) Withdrawn(IEnumerable<string> queued, IEnumerable<QueueItem> queue, IEnumerable<GameState> games, Settings settings)
	{
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		List<string> list3 = list;
		foreach (string id in queued)
		{
			QueueItem queueItem = queue.FirstOrDefault((QueueItem q) => q.GameId == id);
			if ((object)queueItem == null || queueItem.Stage != QueueStage.Waiting || !(queueItem.Note == "starts when the PC is idle"))
			{
				list2.Add(id);
				continue;
			}
			GameState gameState = games.FirstOrDefault((GameState s) => s.Game.Id == id);
			bool flag = (object)gameState == null;
			if (!flag)
			{
				NewShadersMode newShadersMode = ModeOf(gameState, settings);
				bool flag2 = (uint)(newShadersMode - 2) <= 1u;
				flag = !flag2;
			}
			if (flag)
			{
				list3.Add(id);
			}
		}
		return (Withdraw: list3, Forget: list2);
	}
}
