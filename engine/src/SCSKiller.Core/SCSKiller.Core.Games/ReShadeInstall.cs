using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SCSKiller.Core.Games;

public sealed record ReShadeInstall(string Dll, bool LoadsAddons, string? Ini, string? Log, IReadOnlyList<ReShadeAddon> Addons, bool BesideExe = true, bool? OptiScalerLoads = null, string? OptiScaler = null, bool OptiScalerIniOdd = false, bool SpecialK = false, bool ProxyLibrary = false)
{
	public string? Loader
	{
		get
		{
			if (OptiScalerLoads != true)
			{
				return null;
			}
			return OptiScaler;
		}
	}

	public ReShadeAddon? ShaderMod
	{
		get
		{
			if (!LoadsAddons)
			{
				return null;
			}
			return Addons.Where((ReShadeAddon a) => (object)a != null && !a.Disabled && a.Kind != AddonKind.NotPipeline).MaxBy((ReShadeAddon a) => a.Kind);
		}
	}

	public LayerBlock Block
	{
		get
		{
			if (BesideExe)
			{
				if (!SpecialK)
				{
					if (OptiScalerLoads != true)
					{
						if (!Loaded.Contains(Path.GetFileName(Dll), StringComparer.OrdinalIgnoreCase))
						{
							if (OptiScalerLoads != false)
							{
								return LayerBlock.UnloadedName;
							}
							return LayerBlock.OptiScalerOff;
						}
						if (!ProxyLibrary)
						{
							return LayerBlock.None;
						}
						return LayerBlock.ProxyLibrary;
					}
					if (!OptiScalerIniOdd)
					{
						return LayerBlock.None;
					}
					return LayerBlock.OptiScalerIni;
				}
				return LayerBlock.SpecialK;
			}
			return LayerBlock.NotBesideExe;
		}
	}

	public bool Copyable => Block == LayerBlock.None;

	public bool Layered
	{
		get
		{
			if (ShaderMod != null)
			{
				return Copyable;
			}
			return false;
		}
	}

	public bool Blocks
	{
		get
		{
			if (!Copyable)
			{
				ReShadeAddon shaderMod = ShaderMod;
				if ((object)shaderMod != null)
				{
					return shaderMod.Kind == AddonKind.LayoutInjecting;
				}
				return false;
			}
			return false;
		}
	}

	public bool AsD3D12
	{
		get
		{
			string fileName = Path.GetFileName(Dll);
			if (!fileName.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase))
			{
				return fileName.Equals("d3d12.scskiller-next.dll", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
	}

	public string? Fingerprint
	{
		get
		{
			if (Layered)
			{
				IEnumerable<string> first = new string[1] { Dll }.Concat(from a in Addons
					where (object)a != null && !a.Disabled && a.Kind != AddonKind.NotPipeline
					select a.Path);
				string loader = Loader;
				IEnumerable<string> second;
				if (loader == null)
				{
					IEnumerable<string> enumerable = Array.Empty<string>();
					second = enumerable;
				}
				else
				{
					IEnumerable<string> enumerable = new _003C_003Ez__ReadOnlyArray<string>(new string[2]
					{
						loader,
						Path.Combine(Path.GetDirectoryName(loader), "OptiScaler.ini")
					});
					second = enumerable;
				}
				return string.Join('|', from p in first.Concat(second)
					select new FileInfo(p) into f
					select $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}");
			}
			return null;
		}
	}

	internal static readonly string[] Loaded = new string[9] { "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "dinput8.dll", "d3d12.scskiller-next.dll", "d3d11.scskiller-next.dll" };
}
