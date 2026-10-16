using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;

namespace System.Text.RegularExpressions.Generated;

[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
[SkipLocalsInit]
internal sealed class _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Domain_0 : Regex
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
				if (num <= inputSpan.Length - 4)
				{
					int num2 = inputSpan.Slice(num).IndexOfAny(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_asciiLettersAndDigitsAndDash);
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
				int pos = 0;
				ReadOnlySpan<char> span = inputSpan.Slice(num);
				if (!_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.IsBoundary(inputSpan, num))
				{
					return false;
				}
				num2 = 0;
				while (true)
				{
					_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.StackPush(ref runstack, ref pos, num);
					num2++;
					int num3 = span.IndexOfAnyExcept(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_asciiLettersAndDigitsAndDash);
					if (num3 < 0)
					{
						num3 = span.Length;
					}
					if (num3 == 0)
					{
						break;
					}
					span = span.Slice(num3);
					num += num3;
					if (span.IsEmpty || span[0] != '.')
					{
						break;
					}
					num++;
					span = inputSpan.Slice(num);
				}
				while (true)
				{
					if (--num2 < 0)
					{
						return false;
					}
					num = runstack[--pos];
					span = inputSpan.Slice(num);
					if (num2 == 0)
					{
						return false;
					}
					int num4 = span.IndexOfAnyExcept(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_asciiLetters);
					if (num4 < 0)
					{
						num4 = span.Length;
					}
					if (num4 >= 2)
					{
						span = span.Slice(num4);
						num += num4;
						if (_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.IsPostWordCharBoundary(inputSpan, num))
						{
							break;
						}
					}
				}
				runtextpos = num;
				Capture(0, start, num);
				return true;
			}
		}

		protected override RegexRunner CreateInstance()
		{
			return new Runner();
		}
	}

	internal static readonly _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Domain_0 Instance = new _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Domain_0();

	private _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Domain_0()
	{
		pattern = "\\b(?:[A-Za-z0-9-]+\\.)+[A-Za-z]{2,}\\b";
		roptions = RegexOptions.None;
		Regex.ValidateMatchTimeout(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout);
		internalMatchTimeout = _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout;
		factory = new RunnerFactory();
		capsize = 1;
	}
}
