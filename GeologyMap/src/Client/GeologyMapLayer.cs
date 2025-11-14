using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent;

public class GeologyMapLayer : RGBMapLayer
{
    public override string Title => "geology";
    public override string LayerGroupCode => "geology";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override EnumMinMagFilter MinFilter => EnumMinMagFilter.Linear;
    public override EnumMinMagFilter MagFilter => EnumMinMagFilter.Nearest;
    public override MapLegendItem[] LegendItems
    {
        get
        {
            throw new NotImplementedException();
        }
    }

    public Dictionary<string, int> colorsByBlockCode = new Dictionary<string, int>();
    public HashSet<string> ignoredBlockCodes = new HashSet<string>();
    public Vec4f OverlayColor = new Vec4f(1, 1, 1, 1);

    private ICoreClientAPI capi;
    private MapDB mapdb;
    private IWorldChunk[] chunksTmp;
    private const int chunksize = 32;
    private float mtThread1secAccum;
    private float genAccum;
    private float diskSaveAccum;
    private object chunksToGenLock = new object();
    private UniqueQueue<FastVec2i> chunksToGen = new UniqueQueue<FastVec2i>();
    private HashSet<FastVec2i> curVisibleChunks = new HashSet<FastVec2i>();
    private ConcurrentQueue<ReadyMapPiece> readyMapPieces = new ConcurrentQueue<ReadyMapPiece>();
    private Dictionary<FastVec2i, MapPieceDB> toSaveList = new Dictionary<FastVec2i, MapPieceDB>();
    private ConcurrentDictionary<FastVec2i, GeologyMultiChunkMapComponent> loadedMapData = new ConcurrentDictionary<FastVec2i, GeologyMultiChunkMapComponent>();


    public string getMapDbFilePath()
    {
        string text = Path.Combine(GamePaths.DataPath, "Maps");
        GamePaths.EnsurePathExists(text);
        return Path.Combine(text, api.World.SavegameIdentifier + "-geology.db");
    }

    public GeologyMapLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
        // Get configurations objects
        foreach (var item in ConfigManager.ConfigInstance.rockCodeColors)
        {
            if (colorsByBlockCode.ContainsKey(item.Key)) continue;

            colorsByBlockCode[item.Key] = ColorUtil.ReverseColorBytes(ColorUtil.Hex2Int(item.Value) | -16777216);
        }

        foreach (var item in ConfigManager.ConfigInstance.ignoredRocks)
        {
            this.ignoredBlockCodes.Add(item);
        }

        OverlayColor.A = ConfigManager.ConfigInstance.overlayAlpha;

        api.Event.ChunkDirty += OnChunkDirty;

        // Client Side Setup
        this.Active = false;
        capi = api as ICoreClientAPI;
        if (api.Side == EnumAppSide.Client)
        {
            api.World.Logger.Notification("Loading world map cache db...");
            mapdb = new MapDB(api.World.Logger);
            string errorMessage = null;
            string mapDbFilePath = getMapDbFilePath();
            mapdb.OpenOrCreate(mapDbFilePath, ref errorMessage, requireWriteAccess: true, corruptionProtection: true, doIntegrityCheck: false);
            if (errorMessage != null)
            {
                throw new Exception($"Cannot open {mapDbFilePath}, possibly corrupted. Please fix manually or delete this file to continue playing");
            }

            api.ChatCommands.GetOrCreate("geomap").BeginSubCommand("purgedb").WithDescription("purge the map db")
                .HandleWith(delegate
                {
                    mapdb.Purge();
                    return TextCommandResult.Success("Ok, db purged");
                })
                .EndSubCommand()
                .BeginSubCommand("redraw")
                .WithDescription("Redraw the map")
                .HandleWith(OnMapCmdRedraw)
                .EndSubCommand();
        }
    }

    private TextCommandResult OnMapCmdRedraw(TextCommandCallingArgs args)
    {
        foreach (GeologyMultiChunkMapComponent value in loadedMapData.Values)
        {
            value.ActuallyDispose();
        }

        loadedMapData.Clear();
        lock (chunksToGenLock)
        {
            foreach (FastVec2i curVisibleChunk in curVisibleChunks)
            {
                chunksToGen.Enqueue(curVisibleChunk.Copy());
            }
        }

        return TextCommandResult.Success("Redrawing map...");
    }

    // Taken from ChunkMapLayer.cs by Anego Studios 
    // https://github.com/anegostudios/vsessentialsmod/blob/e9dbc197df1a329b5b8789e2aa086b525ff4d3c8/Systems/WorldMap/ChunkLayer/ChunkMapLayer.cs#L160
    private void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        lock (chunksToGenLock)
        {
            if (!mapSink.IsOpened) return;

            FastVec2i tmpMccoord = new FastVec2i(chunkCoord.X / MultiChunkMapComponent.ChunkLen, chunkCoord.Z / MultiChunkMapComponent.ChunkLen);
            FastVec2i tmpCoord = new FastVec2i(chunkCoord.X, chunkCoord.Z);

            if (!loadedMapData.ContainsKey(tmpMccoord) && !curVisibleChunks.Contains(tmpCoord)) return;

            chunksToGen.Enqueue(new FastVec2i(chunkCoord.X, chunkCoord.Z));
        }
    }

    // Adapted from ChunkMapLayer.cs by Anego Studios 
    // https://github.com/anegostudios/vsessentialsmod/blob/e9dbc197df1a329b5b8789e2aa086b525ff4d3c8/Systems/WorldMap/ChunkLayer/ChunkMapLayer.cs#L206
    public override void OnLoaded()
    {
        if (api.Side == EnumAppSide.Server)
        {
            return;
        }

        chunksTmp = new IWorldChunk[api.World.BlockAccessor.MapSizeY / 32];

        BlockPos blockPos = new BlockPos(3);
        IList<Block> blocks = api.World.Blocks;
        for (int j = 0; j < blocks.Count; j++)
        {
            Block block = blocks[j];

            if (block.Code == null || !isBlockValid(block) || colorsByBlockCode.ContainsKey(block.Code)) continue;

            int blockColor = block.GetColor(capi, blockPos) | -16777216;
            colorsByBlockCode.Add(block.Code, blockColor);
        }
    }

    public override void ComposeDialogExtras(GuiDialogWorldMap guiDialogWorldMap, GuiComposer compo)
    {
        string key = "worldmap-layer-" + LayerGroupCode;

        ElementBounds dlgBounds =
            ElementStdBounds.AutosizedMainDialog
            .WithFixedPosition(
                (compo.Bounds.renderX + compo.Bounds.OuterWidth) / RuntimeEnv.GUIScale + 10,
                (compo.Bounds.renderY + compo.Bounds.OuterHeight) / RuntimeEnv.GUIScale - 95
            )
            .WithAlignment(EnumDialogArea.None)
        ;

        ElementBounds row = ElementBounds.Fixed(0, 0, 160, 25);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChild(row);


        guiDialogWorldMap.Composers[key] =
            capi.Gui
                .CreateCompo(key, dlgBounds)
                .AddShadedDialogBG(bgBounds, false)
                .AddDialogTitleBar(Lang.Get("maplayer-"+LayerGroupCode), () => { guiDialogWorldMap.Composers[key].Enabled = false; })
                .BeginChildElements(bgBounds)
                    .AddSlider((newValue) => { ConfigManager.ConfigInstance.overlayAlpha = newValue / 100.0f; OverlayColor.A = ConfigManager.ConfigInstance.overlayAlpha; return true; }, row = row.BelowCopy(0, 5).WithFixedSize(125, 25), "alpha-slider")
                .EndChildElements()
                .Compose()
        ;

        guiDialogWorldMap.Composers[key].GetSlider("alpha-slider").SetValues((int)(ConfigManager.ConfigInstance.overlayAlpha * 100), 0, 100, 1);

        guiDialogWorldMap.Composers[key].Enabled = true;
    }

    public override void OnMapClosedClient()
    {
        lock (chunksToGenLock)
        {
            chunksToGen.Clear();
        }

        curVisibleChunks.Clear();

        ConfigManager.SaveModConfig(api);
    }

    public override void Dispose()
    {
        if (loadedMapData != null)
        {
            foreach (GeologyMultiChunkMapComponent value in loadedMapData.Values)
            {
                value?.ActuallyDispose();
            }
        }

        MultiChunkMapComponent.DisposeStatic();
        base.Dispose();
    }

    public override void OnShutDown()
    {
        MultiChunkMapComponent.tmpTexture?.Dispose();
        mapdb?.Dispose();
    }

    // Taken from ChunkMapLayer.cs by Anego Studios 
    // https://github.com/anegostudios/vsessentialsmod/blob/e9dbc197df1a329b5b8789e2aa086b525ff4d3c8/Systems/WorldMap/ChunkLayer/ChunkMapLayer.cs#L284
    public override void OnOffThreadTick(float dt)
    {
        genAccum += dt;
        if (genAccum < 0.1) return;
        genAccum = 0;

        int quantityToGen = chunksToGen.Count;
        while (quantityToGen > 0)
        {
            if (mapSink.IsShuttingDown) break;

            quantityToGen--;
            FastVec2i cord;

            lock (chunksToGenLock)
            {
                if (chunksToGen.Count == 0) break;
                cord = chunksToGen.Dequeue();
            }

            if (!api.World.BlockAccessor.IsValidPos(cord.X * chunksize, 1, cord.Y * chunksize)) continue;

            IMapChunk mc = api.World.BlockAccessor.GetMapChunk(cord.X, cord.Y);
            if (mc == null)
            {
                try
                {
                    MapPieceDB piece = mapdb.GetMapPiece(cord);
                    if (piece?.Pixels != null)
                    {
                        loadFromChunkPixels(cord, piece.Pixels);
                    }
                }
                catch (ProtoBuf.ProtoException)
                {
                    api.Logger.Warning("Failed loading map db section {0}/{1}, a protobuf exception was thrown. Will ignore.", cord.X, cord.Y);
                }
                catch (OverflowException)
                {
                    api.Logger.Warning("Failed loading map db section {0}/{1}, a overflow exception was thrown. Will ignore.", cord.X, cord.Y);
                }

                continue;
            }

            int[] tintedPixels = GenerateChunkImage(cord, mc);
            if (tintedPixels == null)
            {
                lock (chunksToGenLock)
                {
                    chunksToGen.Enqueue(cord);
                }

                continue;
            }

            toSaveList[cord.Copy()] = new MapPieceDB() { Pixels = tintedPixels };

            loadFromChunkPixels(cord, tintedPixels);
        }

        if (toSaveList.Count > 100 || diskSaveAccum > 4f)
        {
            diskSaveAccum = 0;
            mapdb.SetMapPieces(toSaveList);
            toSaveList.Clear();
        }
    }

    // Taken from ChunkMapLayer.cs by Anego Studios 
    // https://github.com/anegostudios/vsessentialsmod/blob/e9dbc197df1a329b5b8789e2aa086b525ff4d3c8/Systems/WorldMap/ChunkLayer/ChunkMapLayer.cs#L354
    public override void OnTick(float dt)
    {
        if (!readyMapPieces.IsEmpty)
        {
            int q = Math.Min(readyMapPieces.Count, 200);
            List<MultiChunkMapComponent> modified = new();
            while (q-- > 0)
            {
                if (readyMapPieces.TryDequeue(out var mappiece))
                {
                    FastVec2i mcord = new FastVec2i(mappiece.Cord.X / MultiChunkMapComponent.ChunkLen, mappiece.Cord.Y / MultiChunkMapComponent.ChunkLen);
                    FastVec2i baseCord = new FastVec2i(mcord.X * MultiChunkMapComponent.ChunkLen, mcord.Y * MultiChunkMapComponent.ChunkLen);

                    if (!loadedMapData.TryGetValue(mcord, out GeologyMultiChunkMapComponent mccomp))
                    {
                        loadedMapData[mcord] = mccomp = new GeologyMultiChunkMapComponent(api as ICoreClientAPI, baseCord, this);
                    }

                    mccomp.setChunk(mappiece.Cord.X - baseCord.X, mappiece.Cord.Y - baseCord.Y, mappiece.Pixels);
                    modified.Add(mccomp);
                }
            }

            foreach (var mccomp in modified) mccomp.FinishSetChunks();
        }

        mtThread1secAccum += dt;
        if (mtThread1secAccum > 1)
        {
            List<FastVec2i> toRemove = new List<FastVec2i>();

            foreach (var val in loadedMapData)
            {
                MultiChunkMapComponent mcmp = val.Value;

                if (!mcmp.AnyChunkSet || !mcmp.IsVisible(curVisibleChunks))
                {
                    mcmp.TTL -= 1;

                    if (mcmp.TTL <= 0)
                    {
                        FastVec2i mccord = val.Key;
                        toRemove.Add(mccord);
                        mcmp.ActuallyDispose();
                    }
                }
                else
                {
                    mcmp.TTL = MultiChunkMapComponent.MaxTTL;
                }
            }

            foreach (var val in toRemove)
            {
                loadedMapData.TryRemove(val, out _);
            }

            mtThread1secAccum = 0;
        }
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!base.Active)
        {
            return;
        }

        foreach (KeyValuePair<FastVec2i, GeologyMultiChunkMapComponent> loadedMapDatum in loadedMapData)
        {
            loadedMapDatum.Value.Render(mapElem, dt);
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        if (!base.Active)
        {
            return;
        }

        foreach (KeyValuePair<FastVec2i, GeologyMultiChunkMapComponent> loadedMapDatum in loadedMapData)
        {
            loadedMapDatum.Value.OnMouseMove(args, mapElem, hoverText);
        }
    }

    public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
    {
        if (!base.Active)
        {
            return;
        }

        foreach (KeyValuePair<FastVec2i, GeologyMultiChunkMapComponent> loadedMapDatum in loadedMapData)
        {
            loadedMapDatum.Value.OnMouseUpOnElement(args, mapElem);
        }
    }

    private void loadFromChunkPixels(FastVec2i cord, int[] pixels)
    {
        readyMapPieces.Enqueue(new ReadyMapPiece
        {
            Pixels = pixels,
            Cord = cord
        });
    }

    // Taken from ChunkMapLayer.cs by Anego Studios 
    // https://github.com/anegostudios/vsessentialsmod/blob/e9dbc197df1a329b5b8789e2aa086b525ff4d3c8/Systems/WorldMap/ChunkLayer/ChunkMapLayer.cs#L452
    public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
    {
        foreach (var val in nowVisible)
        {
            curVisibleChunks.Add(val);
        }

        foreach (var val in nowHidden)
        {
            curVisibleChunks.Remove(val);
        }

        lock (chunksToGenLock)
        {
            foreach (FastVec2i cord in nowVisible)
            {
                FastVec2i tmpMccoord = new FastVec2i(cord.X / MultiChunkMapComponent.ChunkLen, cord.Y / MultiChunkMapComponent.ChunkLen);

                int dx = cord.X % MultiChunkMapComponent.ChunkLen;
                int dz = cord.Y % MultiChunkMapComponent.ChunkLen;
                if (dx < 0 || dz < 0) continue;

                if (loadedMapData.TryGetValue(tmpMccoord, out GeologyMultiChunkMapComponent mcomp))
                {
                    if (mcomp.IsChunkSet(dx, dz)) continue;
                }

                chunksToGen.Enqueue(cord.Copy());
            }
        }

        foreach (FastVec2i cord in nowHidden)
        {
            if (cord.X < 0 || cord.Y < 0) continue;

            FastVec2i mcord = new FastVec2i(cord.X / MultiChunkMapComponent.ChunkLen, cord.Y / MultiChunkMapComponent.ChunkLen);

            if (loadedMapData.TryGetValue(mcord, out GeologyMultiChunkMapComponent mc))
            {
                mc.unsetChunk(cord.X % MultiChunkMapComponent.ChunkLen, cord.Y % MultiChunkMapComponent.ChunkLen);
            }
        }
    }

    // Adapted from ChunkMapLayer.cs by Anego Studios
    // https://github.com/anegostudios/vsessentialsmod/blob/e9dbc197df1a329b5b8789e2aa086b525ff4d3c8/Systems/WorldMap/ChunkLayer/ChunkMapLayer.cs#L507
    public int[] GenerateChunkImage(FastVec2i chunkPos, IMapChunk mc)
    {
        Vec2i vec2i = new Vec2i();

        for (int i = 0; i < chunksTmp.Length; i++)
        {
            chunksTmp[i] = capi.World.BlockAccessor.GetChunk(chunkPos.X, i, chunkPos.Y);
            if (chunksTmp[i] == null || !(chunksTmp[i] as IClientChunk).LoadedFromServer)
            {
                return null;
            }
        }

        int[] resultPixelArray = new int[1024];
        for (int k = 0; k < resultPixelArray.Length; k++)
        {
            int topBlockHeigh = mc.RainHeightMap[k];
            int topChunkIndex = topBlockHeigh / 32;
            if (topChunkIndex >= chunksTmp.Length)
            {
                continue;
            }

            MapUtil.PosInt2d(k, 32L, vec2i);
            int index = chunksTmp[topChunkIndex].UnpackAndReadBlock(MapUtil.Index3d(vec2i.X, topBlockHeigh % 32, vec2i.Y, 32, 32), 3);
            Block block = api.World.Blocks[index];

            while (topBlockHeigh > 0 && !isBlockValid(block))
            {
                topBlockHeigh--;
                topChunkIndex = topBlockHeigh / 32;
                index = chunksTmp[topChunkIndex].UnpackAndReadBlock(MapUtil.Index3d(vec2i.X, topBlockHeigh % 32, vec2i.Y, 32, 32), 3);
                block = api.World.Blocks[index];
            }

            if (topBlockHeigh <= 0 || !colorsByBlockCode.TryGetValue(block.Code, out int blockColor))
            {
                resultPixelArray[k] = 0;
            }
            else
            {
                resultPixelArray[k] = blockColor;
            }
        }

        for (int n = 0; n < chunksTmp.Length; n++)
        {
            chunksTmp[n] = null;
        }

        return resultPixelArray;
    }

    private bool isBlockValid(Block block)
    {
        return block.BlockMaterial == EnumBlockMaterial.Stone
            && block.Code.PathStartsWith("rock-")
            && !ignoredBlockCodes.Contains(block.Code);
    }
}