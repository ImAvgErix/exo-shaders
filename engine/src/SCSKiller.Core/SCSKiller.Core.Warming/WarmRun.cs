using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Warming;

internal sealed class WarmRun : IWarmRun
{
	private sealed class Adjusted(IProgress<WarmProgress> inner, long carried, long carriedSkips, string? note, long? total) : IProgress<WarmProgress>
	{
		public void Report(WarmProgress p)
		{
			inner.Report(p with
			{
				Total = (total ?? p.Total),
				Failed = p.Failed + carried,
				DriverSkipped = p.DriverSkipped + carriedSkips,
				Note = note
			});
		}
	}

	private static readonly TimeSpan CacheSampleInterval = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250L);

	private const int MaxProcesses = 256;

	private const int MaxKnownSkips = 8;

	private Process _p;

	private readonly EventWaitHandle _stop;

	private readonly object _lock = new object();

	private readonly TimeSpan _stuckAfter;

	private readonly TaskCompletionSource _doneSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

	private bool _paused;

	private bool _stopping;

	private volatile string? _ended;

	public Task<WarmResult> Completion { get; }

	public WarmRun(IGpuVendorBackend vendor, GpuInfo gpu, string exe, Game game, string workDir, WarmOptions o, IProgress<WarmProgress>? progress, TimeSpan stuckAfter, string? stagePath, int maxRecoveries, IReadOnlyDictionary<string, string>? env, string[]? extra = null, IProgress<string>? log = null)
	{
		WarmRun warmRun = this;
		_stuckAfter = stuckAfter;
		string stopName = $"Local\\SCSKiller.Stop.{Guid.NewGuid():N}";
		_stop = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, stopName);
		long before = vendor.GetCacheUsage().BytesOnDisk;
		Stopwatch clock = Stopwatch.StartNew();
		ConcurrentQueue<string> stderr = new ConcurrentQueue<string>();
		HashSet<string> skipKeys = new HashSet<string>(o.SkipKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		WarmPasses passes = WarmPasses.Read(workDir);
		(int, long) tuple = passes?.Locate(o.StartAt) ?? (0, o.StartAt);
		int pass = tuple.Item1;
		long from = tuple.Item2;
		_p = Launch(from, 0, new List<long>(), Array.Empty<long>());
		Completion = Task.Run<WarmResult>(async () =>
		{
			try
			{
				return await Run();
				async Task<WarmResult> Run()
				{
					Stopwatch sampled = null;
					long growth = 0L;
					long carried = 0L;
					long carriedSkips = 0L;
					string text = null;
					List<long> skip = new List<long>();
					HashSet<string> crashed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					int removals = 0;
					int knownSkips = 0;
					bool betweenPasses = false;
					bool resumable = false;
					string stage = null;
					int n = 1;
					WarmEvent last;
					WarmEvent done;
					string error;
					int exitCode;
					bool flag;
					while (true)
					{
						string text2;
						if (passes == null)
						{
							text2 = null;
						}
						else
						{
							text2 = (passes.IsFast(pass) ? "careful compile: the plan's other pipelines at full speed" : $"careful compile: recorded pipelines, pass {passes.Number(pass)} of {passes.CarefulCount}");
						}
						Adjusted progress2 = ((progress == null) ? null : new Adjusted(progress, carried, carriedSkips, text ?? text2, passes?.Total));
						bool lastPass = passes == null || pass == passes.Count - 1;
						stage = null;
						(last, done, error) = await WarmOutput.Pump(warmRun._p.StandardOutput, progress2, Growth, ReportEvery, () =>
						{
							if (lastPass || warmRun._stopping)
							{
								warmRun._doneSeen.TrySetResult();
							}
						}, Whole, (string s) =>
						{
							stage = s;
						});
						await warmRun._p.WaitForExitAsync();
						exitCode = warmRun._p.ExitCode;
						flag = false;
						crashed.UnionWith(done?.Crashed ?? Array.Empty<string>());
						if (error == null && exitCode == 0 && (object)done != null && done.Event == "done" && !done.Stopped && !lastPass)
						{
							long num = carried + done.Failed;
							long num2 = carriedSkips + done.DriverSkipped;
							flag = true;
							carriedSkips = num2;
							carried = num;
							lock (warmRun._lock)
							{
								if (warmRun._stopping)
								{
									betweenPasses = true;
									break;
								}
								int num3 = pass + 1;
								removals = 0;
								text = null;
								n = 0;
								pass = num3;
								skip.Clear();
								skipKeys.UnionWith(crashed);
								warmRun._p = Launch(0L, 0, skip, Array.Empty<long>());
							}
						}
						else
						{
							if ((object)done == null || !(done.Event == "retry"))
							{
								break;
							}
							WarmEvent warmEvent = done;
							if (error != null)
							{
								break;
							}
							long num2 = carried + warmEvent.Failed;
							long num = carriedSkips + warmEvent.DriverSkipped;
							flag = true;
							carriedSkips = num;
							carried = num2;
							if (warmEvent.FailedItem >= 0)
							{
								skip.Add(warmEvent.FailedItem);
							}
							skip.AddRange(warmEvent.Skip ?? Array.Empty<long>());
							int count = skipKeys.Count;
							skipKeys.UnionWith(warmEvent.Crashed ?? Array.Empty<string>());
							bool flag2;
							switch (warmEvent.Reason)
							{
							case "removed":
							case "fault":
							case "died":
								flag2 = true;
								break;
							default:
								flag2 = false;
								break;
							}
							bool flag3 = flag2;
							bool flag4 = warmEvent.Reason == "hang";
							int num4 = warmEvent.RtThreads;
							if (warmEvent.FailedItem >= 0)
							{
								int num3 = knownSkips + 1;
								knownSkips = num3;
								if (num3 > 8)
								{
									num4 = Math.Max(1, num4 / 4);
								}
							}
							lock (warmRun._lock)
							{
								int num5;
								if (flag3 | flag4)
								{
									if (skipKeys.Count > count)
									{
										goto IL_0670;
									}
									if (warmEvent.Reason == "died")
									{
										IReadOnlyList<long> skip2 = warmEvent.Skip;
										if (skip2 != null && skip2.Count > 0)
										{
											goto IL_0670;
										}
									}
									num5 = removals + 1;
									goto IL_0671;
								}
								goto IL_0676;
								IL_0676:
								if (warmRun._stopping || n >= 256 || removals > maxRecoveries)
								{
									if (warmRun._stopping)
									{
										error = null;
									}
									else if (flag3)
									{
										error = Warmer.CrashedError(crashed.Count);
									}
									else
									{
										error = (flag4 ? "the GPU driver kept hanging, so the compile stopped. Compiling again continues where it stopped" : $"gave up after {n} processes (the driver keeps faulting)");
									}
									resumable = (flag3 | flag4) && !warmRun._stopping;
									break;
								}
								if (flag3)
								{
									text = "recovering from a GPU driver crash";
								}
								else if (flag4)
								{
									text = "skipping pipelines that hung the GPU driver";
								}
								else
								{
									text = ((warmEvent.FailedItem >= 0) ? $"skipping a ray tracing state object that crashed ({num4} {((num4 == 1) ? "thread" : "threads")})" : $"retrying ray tracing with fewer threads ({num4})");
								}
								progress?.Report(new WarmProgress(Whole(warmEvent.Done), passes?.Total ?? warmEvent.Total, carried, 0.0, Growth(), 0L, text, 0, 0, carriedSkips));
								WarmRun warmRun2 = warmRun;
								long start = warmEvent.From;
								int rtThreads = num4;
								object isolate;
								if (!flag3)
								{
									IReadOnlyList<long> skip2 = Array.Empty<long>();
									isolate = skip2;
								}
								else
								{
									isolate = warmEvent.Isolate ?? Array.Empty<long>();
								}
								warmRun2._p = Launch(start, rtThreads, skip, (IReadOnlyList<long>)isolate);
								goto end_IL_061c;
								IL_0670:
								num5 = 0;
								goto IL_0671;
								IL_0671:
								removals = num5;
								goto IL_0676;
								end_IL_061c:;
							}
						}
						n++;
						continue;
						long Growth()
						{
							if (sampled == null || sampled.Elapsed >= CacheSampleInterval)
							{
								long num6 = Math.Max(0L, vendor.GetCacheUsage().BytesOnDisk - before);
								Stopwatch stopwatch = Stopwatch.StartNew();
								sampled = stopwatch;
								growth = num6;
							}
							return growth;
						}
					}
					warmRun._stop.Dispose();
					growth = Math.Max(0L, vendor.GetCacheUsage().BytesOnDisk - before);
					bool flag5 = ((object)done != null && done.Event == "retry" && warmRun._stopping) | betweenPasses;
					WarmOutcome warmOutcome = ((error != null || (!flag5 && (exitCode != 0 || !(done != null)))) ? WarmOutcome.Failed : ((flag5 || done.Stopped) ? WarmOutcome.Stopped : WarmOutcome.Completed));
					if (warmRun._ended != null)
					{
						string ended = warmRun._ended;
						error = ended;
						warmOutcome = WarmOutcome.Failed;
					}
					if (warmOutcome == WarmOutcome.Failed && error == null)
					{
						error = ((exitCode != 0) ? $"scskiller_warm exited with code {exitCode}{(stderr.IsEmpty ? "" : (": " + string.Join(" | ", stderr.TakeLast(3))))}" : "scskiller_warm exited without a done event");
					}
					long failed = carried + (flag ? 0 : (last?.Failed ?? 0));
					long driverSkipped = carriedSkips + (flag ? 0 : (last?.DriverSkipped ?? 0));
					return new WarmResult(warmOutcome, Whole(last?.Done ?? from), passes?.Total ?? last?.Total ?? 0, failed, clock.Elapsed, growth, Path.Combine(stage ?? Path.Combine(workDir, "stage"), "scskiller.log"), (warmOutcome == WarmOutcome.Failed) ? error : null, 0L, (crashed.Count > 0) ? crashed : null, driverSkipped, resumable && warmOutcome == WarmOutcome.Failed && warmRun._ended == null);
					long Whole(long num6)
					{
						return passes?.Overall(pass, num6) ?? num6;
					}
				}
			}
			catch
			{
				Process p;
				lock (warmRun._lock)
				{
					p = warmRun._p;
				}
				try
				{
					p.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
				}
				throw;
			}
		});
		Process Launch(long start, int rtThreads, List<long> skip, IReadOnlyList<long> isolate)
		{
			from = start;
			ProcessStartInfo processStartInfo = new ProcessStartInfo(exe)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				WorkingDirectory = workDir
			};
			int num = ((passes != null && !passes.IsFast(pass) && o.CarefulThreads > 0) ? o.CarefulThreads : o.Threads);
			string[] array = new string[12]
			{
				workDir,
				Path.GetFileName(game.ExePath),
				"--threads",
				num.ToString(),
				"--priority",
				(o.Priority == WarmPriority.Idle) ? "idle" : "below",
				"--start",
				start.ToString(),
				"--stop-event",
				stopName,
				"--adapter-luid",
				gpu.AdapterLuid.ToString("X16")
			};
			foreach (string item in array)
			{
				processStartInfo.ArgumentList.Add(item);
			}
			if (o.MemoryMB > 0)
			{
				processStartInfo.ArgumentList.Add("--memory-mb");
				processStartInfo.ArgumentList.Add(o.MemoryMB.ToString());
			}
			if (rtThreads > 0)
			{
				processStartInfo.ArgumentList.Add("--rt-threads");
				processStartInfo.ArgumentList.Add(rtThreads.ToString());
			}
			if (skip.Count > 0)
			{
				processStartInfo.ArgumentList.Add("--skip");
				processStartInfo.ArgumentList.Add(string.Join(',', skip));
			}
			if (stagePath != null)
			{
				processStartInfo.ArgumentList.Add("--stage-path");
				processStartInfo.ArgumentList.Add(stagePath);
			}
			array = extra ?? Array.Empty<string>();
			foreach (string item2 in array)
			{
				processStartInfo.ArgumentList.Add(item2);
			}
			if (vendor.Caps.PackageKeyed)
			{
				string text = XboxSource.AppUserModelId(game);
				if (text != null)
				{
					processStartInfo.ArgumentList.Add("--package");
					processStartInfo.ArgumentList.Add(text);
				}
			}
			if (skipKeys.Count > 0)
			{
				string text2 = string.Join(',', skipKeys.Order(StringComparer.Ordinal));
				if (text2.Length > 8000)
				{
					string fullPath = Path.GetFullPath(Path.Combine(workDir, "skip-keys.txt"));
					File.WriteAllText(fullPath, text2);
					text2 = "@" + fullPath;
				}
				processStartInfo.ArgumentList.Add("--skip-keys");
				processStartInfo.ArgumentList.Add(text2);
			}
			if (isolate.Count > 0)
			{
				processStartInfo.ArgumentList.Add("--isolate");
				processStartInfo.ArgumentList.Add(string.Join(',', isolate));
			}
			foreach (var (key, value) in env ?? new Dictionary<string, string>())
			{
				processStartInfo.Environment[key] = value;
			}
			if (passes != null)
			{
				processStartInfo.ArgumentList.Add("--pass");
				processStartInfo.ArgumentList.Add(passes.Number(pass).ToString());
			}
			Process process = Process.Start(processStartInfo) ?? throw new InvalidOperationException("could not start " + exe);
			try
			{
				WarmJob.Current.Add(process.Id);
			}
			catch (Exception)
			{
				process.Kill(entireProcessTree: true);
				throw;
			}
			process.ErrorDataReceived += (object _, DataReceivedEventArgs e) =>
			{
				string data = e.Data;
				if (data != null && data.Length > 0)
				{
					stderr.Enqueue(data);
					log?.Report(game.Name + ": compile: " + data);
				}
			};
			process.BeginErrorReadLine();
			return process;
		}
	}

	public void Pause()
	{
		lock (_lock)
		{
			if (!_paused && !_stopping && !_p.HasExited)
			{
				ProcessTree.Suspend(_p.Id, suspend: true);
				_paused = true;
			}
		}
	}

	public void Resume()
	{
		lock (_lock)
		{
			if (_paused)
			{
				ProcessTree.Suspend(_p.Id, suspend: false);
				_paused = false;
			}
		}
	}

	public void Stop()
	{
		lock (_lock)
		{
			if (_stopping || Completion.IsCompleted)
			{
				return;
			}
			_stopping = true;
		}
		Resume();
		try
		{
			_stop.Set();
		}
		catch (ObjectDisposedException)
		{
			return;
		}
		Task.Run(async () =>
		{
			InlineArray3<Task> buffer = default;
			buffer[0] = _doneSeen.Task;
			buffer[1] = Completion;
			buffer[2] = Task.Delay(_stuckAfter);
			await Task.WhenAny(buffer);
			if (_doneSeen.Task.IsCompleted || Completion.IsCompleted)
			{
				return;
			}
			Process p;
			lock (_lock)
			{
				p = _p;
			}
			_ended = "the compile didn't stop cleanly (the driver may be stuck), so it was ended";
			try
			{
				p.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}
		});
	}
}
