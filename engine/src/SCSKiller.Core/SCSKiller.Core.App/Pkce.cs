using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace SCSKiller.Core.App;

public static class Pkce
{
	public static string Random(int bytes)
	{
		return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
	}

	public static string Challenge(string verifier)
	{
		return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
	}
}
