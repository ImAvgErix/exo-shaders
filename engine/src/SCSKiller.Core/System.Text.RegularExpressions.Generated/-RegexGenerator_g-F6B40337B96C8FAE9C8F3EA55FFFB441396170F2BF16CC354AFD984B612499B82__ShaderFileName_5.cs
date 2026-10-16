using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;

namespace System.Text.RegularExpressions.Generated;

[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
[SkipLocalsInit]
internal sealed class _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__ShaderFileName_5 : Regex
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
				if (num <= inputSpan.Length - 23)
				{
					ReadOnlySpan<char> readOnlySpan = inputSpan.Slice(num);
					int num2;
					for (num2 = 0; num2 < readOnlySpan.Length - 22; num2++)
					{
						int num3 = readOnlySpan.Slice(num2).IndexOfAny('G', 'S');
						if (num3 < 0)
						{
							break;
						}
						num2 += num3;
						if ((uint)(num2 + 9) >= (uint)readOnlySpan.Length)
						{
							break;
						}
						char c;
						if ((((c = readOnlySpan[num2 + 6]) == 'A') | (c == 'S')) && (((c = readOnlySpan[num2 + 9]) == 'd') | (c == 'h')))
						{
							runtextpos = num + num2;
							return true;
						}
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
				ReadOnlySpan<char> span = inputSpan.Slice(num);
				if (span.IsEmpty)
				{
					UncaptureUntil(0);
					return false;
				}
				switch (span[0])
				{
				case 'S':
				{
					if (!span.Slice(1).StartsWith("haderArchive-"))
					{
						UncaptureUntil(0);
						return false;
					}
					num += 14;
					span = inputSpan.Slice(num);
					num2 = num;
					int j;
					for (j = 0; (uint)j < (uint)span.Length; j++)
					{
						char c;
						if (!(((c = span[j]) < '\u0080') ? (("\0\0\u2000Ͽ\ufffe蟿\ufffe߿"[(int)c >> 4] & (1 << (c & 0xF))) != 0) : RegexRunner.CharInClass(c, "\0\u0002\n-.\0\u0002\u0004\u0005\u0003\u0001\u0006\t\u0013\0")))
						{
							break;
						}
					}
					if (j == 0)
					{
						UncaptureUntil(0);
						return false;
					}
					span = span.Slice(j);
					num += j;
					Capture(1, num2, num);
					if (!span.StartsWith(".ushaderbytecode"))
					{
						UncaptureUntil(0);
						return false;
					}
					num += 16;
					span = inputSpan.Slice(num);
					break;
				}
				case 'G':
				{
					if (!span.Slice(1).StartsWith("lobalShaderCache-"))
					{
						UncaptureUntil(0);
						return false;
					}
					num += 18;
					span = inputSpan.Slice(num);
					num3 = num;
					int i;
					for (i = 0; (uint)i < (uint)span.Length; i++)
					{
						char c;
						if (!(((c = span[i]) < '\u0080') ? (("\0\0\u2000Ͽ\ufffe蟿\ufffe߿"[(int)c >> 4] & (1 << (c & 0xF))) != 0) : RegexRunner.CharInClass(c, "\0\u0002\n-.\0\u0002\u0004\u0005\u0003\u0001\u0006\t\u0013\0")))
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
					Capture(2, num3, num);
					if (!span.StartsWith(".bin"))
					{
						UncaptureUntil(0);
						return false;
					}
					num += 4;
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

	internal static readonly _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__ShaderFileName_5 Instance = new _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__ShaderFileName_5();

	private _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__ShaderFileName_5()
	{
		pattern = "ShaderArchive-([\\w-]+)\\.ushaderbytecode|GlobalShaderCache-([\\w-]+)\\.bin";
		roptions = RegexOptions.None;
		Regex.ValidateMatchTimeout(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout);
		internalMatchTimeout = _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout;
		factory = new RunnerFactory();
		capsize = 3;
	}
}
