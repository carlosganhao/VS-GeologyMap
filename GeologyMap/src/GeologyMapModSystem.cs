using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using HarmonyLib;

namespace CustomMapRegions;

public class GeologyMapModSystem : ModSystem
{
    private string patchId = "geology";
    private Harmony harmonyInstance;

    public override void StartPre(ICoreAPI api)
    {
        base.StartPre(api);

        if (api.Side == EnumAppSide.Client)
        {
            ConfigManager.LoadModConfig(api);

            if (!Harmony.HasAnyPatches(patchId))
            {
                harmonyInstance = new Harmony(patchId);
                harmonyInstance.PatchAll();
            }
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        var mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
        mapManager.RegisterMapLayer<GeologyMapLayer>("geology", 1);
    }

    public override void Dispose()
    {
        ConfigManager.ConfigInstance = null;
        harmonyInstance?.UnpatchAll(patchId);
        base.Dispose();
    }
}
