using System.CodeDom.Compiler;
using System.Collections;
using System.Runtime.CompilerServices;

namespace System.Text.RegularExpressions.Generated;

[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
[SkipLocalsInit]
internal sealed class _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Inline_3 : Regex
{
	private sealed class RunnerFactory : RegexRunnerFactory
	{
		private sealed class Runner : RegexRunner
		{
			protected override void Scan(ReadOnlySpan<char> inputSpan)
			{
				while (TryFindNextPossibleStartingPosition(inputSpan) && !TryMatchAtCurrentPosition(inputSpan) && runtextpos != inputSpan.Length)
				{
					runtextpos++;
					if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_hasTimeout)
					{
						CheckTimeout();
					}
				}
			}

			private bool TryFindNextPossibleStartingPosition(ReadOnlySpan<char> inputSpan)
			{
				int num = runtextpos;
				if (num <= inputSpan.Length - 3)
				{
					int num2 = inputSpan.Slice(num).IndexOfAny('*', '[', '`');
					if (num2 >= 0)
					{
						runtextpos = num + num2;
						return true;
					}
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
				ReadOnlySpan<char> span = inputSpan.Slice(num);
				if (span.IsEmpty)
				{
					UncaptureUntil(0);
					return false;
				}
				switch (span[0])
				{
				case '*':
				{
					if ((uint)span.Length < 2u || span[1] != '*')
					{
						UncaptureUntil(0);
						return false;
					}
					num += 2;
					span = inputSpan.Slice(num);
					num2 = num;
					int num7 = span.IndexOf('*');
					if (num7 < 0)
					{
						num7 = span.Length;
					}
					if (num7 == 0)
					{
						UncaptureUntil(0);
						return false;
					}
					span = span.Slice(num7);
					num += num7;
					Capture(1, num2, num);
					if (!span.StartsWith("**"))
					{
						UncaptureUntil(0);
						return false;
					}
					num += 2;
					span = inputSpan.Slice(num);
					break;
				}
				case '`':
				{
					num++;
					span = inputSpan.Slice(num);
					num3 = num;
					int num8 = span.IndexOf('`');
					if (num8 < 0)
					{
						num8 = span.Length;
					}
					if (num8 == 0)
					{
						UncaptureUntil(0);
						return false;
					}
					span = span.Slice(num8);
					num += num8;
					Capture(2, num3, num);
					if (span.IsEmpty || span[0] != '`')
					{
						UncaptureUntil(0);
						return false;
					}
					num++;
					span = inputSpan.Slice(num);
					break;
				}
				case '[':
				{
					num++;
					span = inputSpan.Slice(num);
					num4 = num;
					int num6 = span.IndexOf(']');
					if (num6 < 0)
					{
						num6 = span.Length;
					}
					if (num6 == 0)
					{
						UncaptureUntil(0);
						return false;
					}
					span = span.Slice(num6);
					num += num6;
					Capture(3, num4, num);
					if (!span.StartsWith("]("))
					{
						UncaptureUntil(0);
						return false;
					}
					num += 2;
					span = inputSpan.Slice(num);
					num5 = num;
					if (!span.StartsWith("https://"))
					{
						UncaptureUntil(0);
						return false;
					}
					num += 8;
					span = inputSpan.Slice(num);
					int i;
					for (i = 0; (uint)i < (uint)span.Length; i++)
					{
						char c;
						if (!(((c = span[i]) < '\u0080') ? (("쇿\uffff﷾\uffff\uffff\uffff\uffff\uffff"[(int)c >> 4] & (1 << (c & 0xF))) != 0) : RegexRunner.CharInClass(c, "\u0001\u0002\u0001)*d")))
						{
							break;
						}
					}
					if (i == 0)
					{
						UncaptureUntil(0);
						return false;
					}
					span = span.Slice(i);
					num += i;
					Capture(4, num5, num);
					if (span.IsEmpty || span[0] != ')')
					{
						UncaptureUntil(0);
						return false;
					}
					num++;
					span = inputSpan.Slice(num);
					break;
				}
				default:
					UncaptureUntil(0);
					return false;
				}
				runtextpos = num;
				Capture(0, start, num);
				return true;
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

	internal static readonly _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Inline_3 Instance = new _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Inline_3();

	private _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Inline_3()
	{
		pattern = "\\*\\*(?<b>[^*]+)\\*\\*|`(?<c>[^`]+)`|\\[(?<t>[^\\]]+)\\]\\((?<u>https://[^)\\s]+)\\)";
		roptions = RegexOptions.None;
		Regex.ValidateMatchTimeout(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout);
		internalMatchTimeout = _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout;
		factory = new RunnerFactory();
		CapNames = new Hashtable
		{
			{ "0", 0 },
			{ "b", 1 },
			{ "c", 2 },
			{ "t", 3 },
			{ "u", 4 }
		};
		capslist = new string[5] { "0", "b", "c", "t", "u" };
		capsize = 5;
	}
}
