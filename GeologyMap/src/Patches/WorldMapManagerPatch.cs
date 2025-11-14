using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

[HarmonyPatch(typeof(WorldMapManager), "getTabsOrdered")]
class WorldMapManagerPatch
{
    static void Postfix(ref List<string> __result)
    {
        string geologyCode = "geology";
        int terrainLayerIndex = __result.FindIndex(x => x.EqualsFast("terrain"));
        __result.Remove(geologyCode);
        __result.Insert(terrainLayerIndex + 1, geologyCode);
    }
}