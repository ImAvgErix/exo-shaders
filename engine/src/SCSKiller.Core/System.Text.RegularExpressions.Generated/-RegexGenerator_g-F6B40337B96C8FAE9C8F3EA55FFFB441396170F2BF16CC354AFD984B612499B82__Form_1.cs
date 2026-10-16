using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;

namespace System.Text.RegularExpressions.Generated;

[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
[SkipLocalsInit]
internal sealed class _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Form_1 : Regex
{
	private sealed class RunnerFactory : RegexRunnerFactory
	{
		private sealed class Runner : RegexRunner
		{
			protected override void Scan(ReadOnlySpan<char> inputSpan)
			{
				if (TryFindNextPossibleStartingPosition(inputSpan) && !TryMatchAtCurrentPosition(inputSpan))
				{
					runtextpos = inputSpan.Length;
				}
			}

			private bool TryFindNextPossibleStartingPosition(ReadOnlySpan<char> inputSpan)
			{
				int num = runtextpos;
				if (num <= inputSpan.Length - 5 && num == 0)
				{
					return true;
				}
				runtextpos = inputSpan.Length;
				return false;
			}

			private bool TryMatchAtCurrentPosition(ReadOnlySpan<char> inputSpan)
			{
				int num = runtextpos;
				int start = num;
				int num2 = 0;
				int num3 = 0;
				int num4 = 0;
				int num5 = 0;
				int num6 = 0;
				int num7 = 0;
				int arg = 0;
				int arg2 = 0;
				int arg3 = 0;
				int arg4 = 0;
				int num8 = 0;
				int num9 = 0;
				int num10 = 0;
				int pos = 0;
				ReadOnlySpan<char> span = inputSpan.Slice(num);
				if (num != 0)
				{
					UncaptureUntil(0);
					return false;
				}
				if (!span.IsEmpty && span[0] == 'v')
				{
					span = span.Slice(1);
					num++;
				}
				num2 = num;
				int i;
				for (i = 0; (uint)i < (uint)span.Length && char.IsDigit(span[i]); i++)
				{
				}
				if (i == 0)
				{
					UncaptureUntil(0);
					return false;
				}
				span = span.Slice(i);
				num += i;
				Capture(1, num2, num);
				if (span.IsEmpty || span[0] != '.')
				{
					UncaptureUntil(0);
					return false;
				}
				num++;
				span = inputSpan.Slice(num);
				num3 = num;
				int j;
				for (j = 0; (uint)j < (uint)span.Length && char.IsDigit(span[j]); j++)
				{
				}
				if (j == 0)
				{
					UncaptureUntil(0);
					return false;
				}
				span = span.Slice(j);
				num += j;
				Capture(2, num3, num);
				if (span.IsEmpty || span[0] != '.')
				{
					UncaptureUntil(0);
					return false;
				}
				num++;
				span = inputSpan.Slice(num);
				num4 = num;
				num6 = num;
				int k;
				for (k = 0; (uint)k < (uint)span.Length && char.IsDigit(span[k]); k++)
				{
				}
				if (k == 0)
				{
					UncaptureUntil(0);
					return false;
				}
				span = span.Slice(k);
				num += k;
				num7 = num;
				num6++;
				while (true)
				{
					num5 = Crawlpos();
					Capture(3, num4, num);
					num8 = 0;
					while (true)
					{
						_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, Crawlpos(), num);
						num8++;
						int num11;
						if (!span.IsEmpty && span[0] == '-')
						{
							num++;
							span = inputSpan.Slice(num);
							num11 = num;
							arg = num;
							int num12 = span.IndexOfAnyExcept(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_asciiLettersAndDigitsAndDash);
							if (num12 < 0)
							{
								num12 = span.Length;
							}
							if (num12 != 0)
							{
								span = span.Slice(num12);
								num += num12;
								arg2 = num;
								arg++;
								goto IL_02d8;
							}
						}
						goto IL_0498;
						IL_04ea:
						num10 = 0;
						while (true)
						{
							_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, Crawlpos(), num);
							num10++;
							if (!span.IsEmpty && span[0] == '+')
							{
								int num13 = span.Slice(1).IndexOfAnyExcept(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_asciiLettersAndDigitsAndDashDot);
								if (num13 < 0)
								{
									num13 = span.Length - 1;
								}
								if (num13 != 0)
								{
									span = span.Slice(num13);
									num += num13;
									num++;
									span = inputSpan.Slice(num);
									if (num10 == 0)
									{
										continue;
									}
									goto IL_05a3;
								}
							}
							goto IL_0569;
							IL_0569:
							if (--num10 < 0)
							{
								break;
							}
							num = runstack[--pos];
							UncaptureUntil(runstack[--pos]);
							span = inputSpan.Slice(num);
							goto IL_05a3;
							IL_05a3:
							if (num >= inputSpan.Length - 1 && ((uint)num >= (uint)inputSpan.Length || inputSpan[num] == '\n'))
							{
								runtextpos = num;
								Capture(0, start, num);
								return true;
							}
							goto IL_0569;
						}
						if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_hasTimeout)
						{
							CheckTimeout();
						}
						if (num8 != 0)
						{
							num11 = runstack[--pos];
							num9 = runstack[--pos];
							if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_hasTimeout)
							{
								CheckTimeout();
							}
							if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_hasTimeout)
							{
								CheckTimeout();
							}
							if (num9 != 0)
							{
								UncaptureUntil(runstack[--pos]);
								_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPop(runstack, ref pos, out arg4, out arg3);
								if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_hasTimeout)
								{
									CheckTimeout();
								}
								if (arg3 < arg4)
								{
									num = --arg4;
									span = inputSpan.Slice(num);
									goto IL_03c1;
								}
								goto IL_03dd;
							}
							goto IL_028b;
						}
						break;
						IL_028b:
						UncaptureUntil(runstack[--pos]);
						_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPop(runstack, ref pos, out arg2, out arg);
						if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_hasTimeout)
						{
							CheckTimeout();
						}
						if (arg < arg2)
						{
							num = --arg2;
							span = inputSpan.Slice(num);
							goto IL_02d8;
						}
						goto IL_0498;
						IL_02d8:
						_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, arg, arg2, Crawlpos());
						num9 = 0;
						goto IL_02f2;
						IL_02f2:
						_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, Crawlpos(), num);
						num9++;
						if (!span.IsEmpty && span[0] == '.')
						{
							num++;
							span = inputSpan.Slice(num);
							arg3 = num;
							int num14 = span.IndexOfAnyExcept(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_asciiLettersAndDigitsAndDash);
							if (num14 < 0)
							{
								num14 = span.Length;
							}
							if (num14 != 0)
							{
								span = span.Slice(num14);
								num += num14;
								arg4 = num;
								arg3++;
								goto IL_03c1;
							}
						}
						goto IL_03dd;
						IL_03dd:
						if (--num9 < 0)
						{
							goto IL_028b;
						}
						num = runstack[--pos];
						UncaptureUntil(runstack[--pos]);
						span = inputSpan.Slice(num);
						_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, num9);
						Capture(4, num11, num);
						_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, num11);
						if (num8 == 0)
						{
							continue;
						}
						goto IL_04ea;
						IL_0498:
						if (--num8 < 0)
						{
							break;
						}
						num = runstack[--pos];
						UncaptureUntil(runstack[--pos]);
						span = inputSpan.Slice(num);
						goto IL_04ea;
						IL_03c1:
						_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, arg3, arg4, Crawlpos());
						goto IL_02f2;
					}
					UncaptureUntil(num5);
					if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_hasTimeout)
					{
						CheckTimeout();
					}
					if (num6 >= num7)
					{
						break;
					}
					num = --num7;
					span = inputSpan.Slice(num);
				}
				UncaptureUntil(0);
				return false;
				[MethodImpl(MethodImplOptions.AggressiveInlining)]
				void UncaptureUntil(int capturePosition)
				{
					while (Crawlpos() > capturePosition)
					{
						Uncapture();
					}
				}
			}
		}

		protected override RegexRunner CreateInstance()
		{
			return new Runner();
		}
	}

	internal static readonly _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Form_1 Instance = new _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Form_1();

	private _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Form_1()
	{
		pattern = "^v?(\\d+)\\.(\\d+)\\.(\\d+)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z.-]+)?$";
		roptions = RegexOptions.None;
		Regex.ValidateMatchTimeout(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout);
		internalMatchTimeout = _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout;
		factory = new RunnerFactory();
		capsize = 5;
	}
}
