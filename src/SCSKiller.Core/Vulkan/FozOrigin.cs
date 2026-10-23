namespace SCSKiller.Core.Vulkan;

/// <summary>Where a Fossilize archive came from: <see cref="Ours"/> = recorded by SCSKiller's layer; <see cref="SteamLocal"/>
/// = recorded on this PC by Steam's layer (steamapp/steamapprun_pipeline_cache); <see cref="SteamDownload"/> = Steam's
/// downloaded steam_pipeline_cache.foz, recorded on other PCs.</summary>
public enum FozOrigin { Ours, SteamLocal, SteamDownload }
