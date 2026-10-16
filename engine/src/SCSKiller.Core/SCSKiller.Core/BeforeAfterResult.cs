namespace SCSKiller.Core;

public sealed record BeforeAfterResult(SessionSnapshot Before, SessionSnapshot After, Comparison Compare);
