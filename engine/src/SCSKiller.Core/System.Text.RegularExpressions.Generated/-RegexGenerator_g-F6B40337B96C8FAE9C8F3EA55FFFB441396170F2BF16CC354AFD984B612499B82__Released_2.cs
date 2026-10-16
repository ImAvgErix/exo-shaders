using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;

namespace System.Text.RegularExpressions.Generated;

[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
[SkipLocalsInit]
internal sealed class _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Released_2 : Regex
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
				if (num <= inputSpan.Length - 10 && num == 0)
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
				ReadOnlySpan<char> span = inputSpan.Slice(num);
				if (num != 0)
				{
					UncaptureUntil(0);
					return false;
				}
				if (!span.StartsWith("## ["))
				{
					UncaptureUntil(0);
					return false;
				}
				num += 4;
				span = inputSpan.Slice(num);
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
				if (span.IsEmpty || span[0] != '.')
				{
					UncaptureUntil(0);
					return false;
				}
				num++;
				span = inputSpan.Slice(num);
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
				if (span.IsEmpty || span[0] != '.')
				{
					UncaptureUntil(0);
					return false;
				}
				num++;
				span = inputSpan.Slice(num);
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
				Capture(1, num2, num);
				if (span.IsEmpty || span[0] != ']')
				{
					UncaptureUntil(0);
					return false;
				}
				Capture(0, start, runtextpos = num + 1);
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

	internal static readonly _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Released_2 Instance = new _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Released_2();

	private _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Released_2()
	{
		pattern = "^## \\[(\\d+\\.\\d+\\.\\d+)\\]";
		roptions = RegexOptions.None;
		Regex.ValidateMatchTimeout(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout);
		internalMatchTimeout = _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout;
		factory = new RunnerFactory();
		capsize = 2;
	}
}
