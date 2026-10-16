using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Compression;

namespace SCSKiller.Core.App;

public static class Codecs
{
	public static readonly IReadOnlyDictionary<string, string> Pinned = new Dictionary<string, string>
	{
		["oodle-data-shared.dll"] = "cba19529d0a3b5ec9c630e95652af01e123ae29a34a8a5f7507f5bcf23d9e82b",
		["zlib-ng2.dll"] = "454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2"
	};

	internal static string? DirOverride;

	private static readonly Lock Gate = new Lock();

	public const string NoNativeOodle = "This CPU lacks AVX2 and BMI2, which the Oodle codec needs.";

	public const string NoOodleDll = "Couldn't download the Oodle codec. Check your antivirus or firewall.";

	public const string OodleUrl = "https://github.com/WorkingRobot/OodleUE/releases/download/2026-06-04-1357/clang-cl-x64-release.zip";

	private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromMinutes(2L),
		MaxResponseContentBufferSize = 268435456L
	};

	public static string Dir => DirOverride ?? Path.Combine(AppStore.DefaultDir, "codecs");

	public static bool NativeOodle
	{
		get
		{
			if (Avx2.IsSupported)
			{
				return Bmi2.X64.IsSupported;
			}
			return false;
		}
	}

	public static IProgress<string>? Log { get; set; }

	public static bool OodleMissing
	{
		get
		{
			using (Gate.EnterScope())
			{
				return Failed.Contains(Path.Combine(Dir, OodleHelper.OodleFileName));
			}
		}
	}

	public static string? Reason(Exception e)
	{
		while (e is AggregateException ex)
		{
			ReadOnlyCollection<Exception> innerExceptions = ex.InnerExceptions;
			if (innerExceptions == null || innerExceptions.Count != 1)
			{
				break;
			}
			e = ex.InnerExceptions[0];
		}
		string message = e.Message;
		if ((!(message == "Couldn't download the Oodle codec. Check your antivirus or firewall.") && !(message == "This CPU lacks AVX2 and BMI2, which the Oodle codec needs.")) || 1 == 0)
		{
			return null;
		}
		return e.Message;
	}

	public static void Load(bool zlib = true)
	{
		using (Gate.EnterScope())
		{
			if (NativeOodle && OodleHelper.Instance == null)
			{
				TryLoad(OodleHelper.OodleFileName, DownloadOodle, (string p) =>
				{
					OodleHelper.Initialize(p);
				}, Log);
			}
			if (zlib && ZlibHelper.Instance == null)
			{
				TryLoad(ZlibHelper.DllName, DownloadZlib, (string p) =>
				{
					ZlibHelper.Initialize(p);
				}, Log);
			}
		}
	}

	internal static bool TryLoad(string name, Func<string, bool> download, Action<string> init, IProgress<string>? log, string? dir = null, string? seedDir = null)
	{
		string item = Path.Combine(dir ?? Dir, name);
		using (Gate.EnterScope())
		{
			if (Failed.Contains(item))
			{
				return false;
			}
			try
			{
				string text = Ensure(name, download, dir, seedDir);
				using (Hold(text, name))
				{
					init(text);
				}
				return true;
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException))
			{
				Failed.Add(item);
				log?.Report($"codec {name} not loaded, decoding in managed code: {ex}");
				return false;
			}
		}
	}

	public static bool DownloadOodle(string path)
	{
		using ZipArchive zipArchive = new ZipArchive(Get("https://github.com/WorkingRobot/OodleUE/releases/download/2026-06-04-1357/clang-cl-x64-release.zip"));
		using Stream stream = (zipArchive.GetEntry("bin/" + OodleHelper.OodleFileName) ?? throw new InvalidDataException("https://github.com/WorkingRobot/OodleUE/releases/download/2026-06-04-1357/clang-cl-x64-release.zip has no " + OodleHelper.OodleFileName)).Open();
		Save(stream, path);
		return true;
	}

	public static bool DownloadZlib(string path)
	{
		using GZipStream gZipStream = new GZipStream(Get("https://github.com/NotOfficer/Zlib-ng.NET/releases/download/1.0.0/zlib-ng2.dll.gz"), CompressionMode.Decompress);
		Save(gZipStream, path);
		return true;
	}

	private static MemoryStream Get(string url)
	{
		try
		{
			return new MemoryStream(Http.GetByteArrayAsync(url).GetAwaiter().GetResult());
		}
		catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) ? true : false)
		{
			throw new IOException("downloading " + url + " failed: " + ex.Message, ex);
		}
	}

	private static void Save(Stream from, string path)
	{
		using FileStream destination = File.Create(path);
		from.CopyTo(destination);
	}

	public static string Ensure(string name, Func<string, bool> download, string? dir = null, string? seedDir = null)
	{
		if (dir == null)
		{
			dir = Dir;
		}
		string text = Path.Combine(dir, name);
		if (Verified(text, name))
		{
			return text;
		}
		Directory.CreateDirectory(dir);
		try
		{
			File.Delete(text);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? true : false) && Verified(text, name))
		{
			return text;
		}
		string text2 = Path.Combine(seedDir ?? AppContext.BaseDirectory, name);
		foreach (string item in Directory.EnumerateFiles(dir, name + ".*.download"))
		{
			try
			{
				if (File.GetLastWriteTimeUtc(item) < DateTime.UtcNow.AddHours(-1.0))
				{
					File.Delete(item);
				}
			}
			catch (IOException)
			{
			}
		}
		string text3 = $"{text}.{Guid.NewGuid():N}.download";
		try
		{
			if (text2 != text && Verified(text2, name))
			{
				File.Copy(text2, text3, overwrite: true);
			}
			else
			{
				download(text3);
			}
			if (!Verified(text3, name))
			{
				throw new InvalidDataException(name + " couldn't be downloaded, or isn't the expected build (SHA-256 mismatch)");
			}
			try
			{
				File.Move(text3, text, overwrite: true);
			}
			catch (Exception ex3) when (((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false) && Verified(text, name))
			{
			}
			return text;
		}
		finally
		{
			File.Delete(text3);
		}
	}

	private static FileStream Hold(string path, string name)
	{
		FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (Pinned.TryGetValue(name, out string value) && Convert.ToHexStringLower(SHA256.HashData(fileStream)) == value)
		{
			return fileStream;
		}
		fileStream.Dispose();
		throw new InvalidDataException(name + " changed after it was verified");
	}

	private static bool Verified(string path, string name)
	{
		if (File.Exists(path) && Pinned.TryGetValue(name, out string value))
		{
			return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == value;
		}
		return false;
	}
}
