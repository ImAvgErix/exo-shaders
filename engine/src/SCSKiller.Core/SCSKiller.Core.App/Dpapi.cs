using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCSKiller.Core.App;

public static class Dpapi
{
	private readonly struct Blob(int size, nint data)
	{
		public readonly int Size = size;

		public readonly nint Data = data;
	}

	private static readonly byte[] Entropy = "SCSKiller.auth.v1"u8.ToArray();

	private const int UiForbidden = 1;

	public static byte[] Protect(byte[] data)
	{
		return Run(data, protect: true);
	}

	public static byte[] Unprotect(byte[] data)
	{
		return Run(data, protect: false);
	}

	public static T? Load<T>(string file) where T : class
	{
		try
		{
			return File.Exists(file) ? JsonSerializer.Deserialize<T>(Unprotect(File.ReadAllBytes(file))) : null;
		}
		catch (Exception ex) when ((ex is CryptographicException || ex is JsonException || ex is IOException) ? true : false)
		{
			return null;
		}
	}

	public static void Save<T>(string file, T value)
	{
		AppStore.WriteAtomic(file, Protect(JsonSerializer.SerializeToUtf8Bytes(value)));
	}

	private unsafe static byte[] Run(byte[] data, bool protect)
	{
		fixed (byte* data2 = data)
		{
			fixed (byte* entropy = Entropy)
			{
				Blob data3 = new Blob(data.Length, (nint)data2);
				Blob entropy2 = new Blob(Entropy.Length, (nint)entropy);
				if (!(protect ? CryptProtectData(in data3, null, in entropy2, 0, 0, 1, out var output) : CryptUnprotectData(in data3, 0, in entropy2, 0, 0, 1, out output)))
				{
					throw new CryptographicException(Marshal.GetLastPInvokeError());
				}
				try
				{
					return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray();
				}
				finally
				{
					LocalFree(output.Data);
				}
			}
		}
	}

	[DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CryptProtectData(in Blob data, string? description, in Blob entropy, nint reserved, nint prompt, int flags, out Blob output);

	[DllImport("crypt32.dll", SetLastError = true)]
	private static extern bool CryptUnprotectData(in Blob data, nint description, in Blob entropy, nint reserved, nint prompt, int flags, out Blob output);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint mem);
}
