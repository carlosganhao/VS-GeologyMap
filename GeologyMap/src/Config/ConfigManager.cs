using System;
using Vintagestory.API.Common;

public static class ConfigManager
{
    public static GeologyMapConfig ConfigInstance { get; internal set; }
    private static string configPath = "geologyMapConfig.json";

    public static void LoadModConfig(ICoreAPI api)
    {
        try
        {
            ConfigInstance = api.LoadModConfig<GeologyMapConfig>(configPath);
            if (ConfigInstance == null)
            {
                ConfigInstance = new GeologyMapConfig();
            }

            api.StoreModConfig<GeologyMapConfig>(ConfigInstance, configPath);
        }
        catch (Exception e)
        {
            api.Logger.Error("[Geology Map] - Could not load config! Loading default settings instead.");
            api.Logger.Error(e);
            ConfigInstance = new GeologyMapConfig();
        }
    }

    public static void SaveModConfig(ICoreAPI api)
    {
        api.StoreModConfig(ConfigInstance, configPath);
    }
}