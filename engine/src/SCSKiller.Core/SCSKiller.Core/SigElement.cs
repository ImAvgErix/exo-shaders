namespace SCSKiller.Core;

public sealed record SigElement(string Semantic, int Index, int Register, byte Mask, int SysValue, int CompType, byte ReadMask = byte.MaxValue, int Interpolation = 0)
{
	public const byte UnknownReadMask = byte.MaxValue;
}
