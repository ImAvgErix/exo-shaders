using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;

namespace System.Text.RegularExpressions.Generated;

[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
[SkipLocalsInit]
internal sealed class _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__LaunchSuffix_4 : Regex
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
				if (num <= inputSpan.Length - 21)
				{
					if (num < inputSpan.Length - 21)
					{
						runtextpos = inputSpan.Length - 21;
					}
					return true;
				}
				runtextpos = inputSpan.Length;
				return false;
			}

			private bool TryMatchAtCurrentPosition(ReadOnlySpan<char> inputSpan)
			{
				int num = runtextpos;
				int start = num;
				ReadOnlySpan<char> span = inputSpan.Slice(num);
				if ((uint)span.Length < 21u || !span.StartsWith("_LRS_") || !char.IsAsciiHexDigit(span[5]) || !char.IsAsciiHexDigit(span[6]) || !char.IsAsciiHexDigit(span[7]) || !char.IsAsciiHexDigit(span[8]) || !char.IsAsciiHexDigit(span[9]) || !char.IsAsciiHexDigit(span[10]) || !char.IsAsciiHexDigit(span[11]) || !char.IsAsciiHexDigit(span[12]) || !char.IsAsciiHexDigit(span[13]) || !char.IsAsciiHexDigit(span[14]) || !char.IsAsciiHexDigit(span[15]) || !char.IsAsciiHexDigit(span[16]) || !char.IsAsciiHexDigit(span[17]) || !char.IsAsciiHexDigit(span[18]) || !char.IsAsciiHexDigit(span[19]) || !char.IsAsciiHexDigit(span[20]))
				{
					return false;
				}
				if (21 < span.Length)
				{
					return false;
				}
				Capture(0, start, runtextpos = num + 21);
				return true;
			}
		}

		protected override RegexRunner CreateInstance()
		{
			return new Runner();
		}
	}

	internal static readonly _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__LaunchSuffix_4 Instance = new _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__LaunchSuffix_4();

	private _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__LaunchSuffix_4()
	{
		pattern = "_LRS_[0-9A-Fa-f]{16}\\z";
		roptions = RegexOptions.None;
		Regex.ValidateMatchTimeout(_003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout);
		internalMatchTimeout = _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Utilities.s_defaultTimeout;
		factory = new RunnerFactory();
		capsize = 1;
	}
}
