using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

class GeologyMultiChunkMapComponent : MultiChunkMapComponent
{
    public GeologyMapLayer mapLayer;
    private int[][] pixelsToSet;
    private int sideLength = 96;
    private Vec3d chunkWorldPos;
    private Vec2f viewPos;
    private Vec3d mouseWorldPos;

    public GeologyMultiChunkMapComponent(ICoreClientAPI capi, FastVec2i baseChunkCord, GeologyMapLayer mapLayer) : base(capi, baseChunkCord)
    {
        this.mapLayer = mapLayer;
        chunkWorldPos = new Vec3d(baseChunkCord.X * 32, 0.0, baseChunkCord.Y * 32);
        viewPos = new Vec2f();
        mouseWorldPos = new Vec3d();
    }

    public override void OnMouseMove(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        viewPos.X = args.X - (float)mapElem.Bounds.renderX;
        viewPos.Y = args.Y - (float)mapElem.Bounds.renderY;
        mapElem.TranslateViewPosToWorldPos(viewPos, ref mouseWorldPos);

        if (mouseWorldPos.X < chunkWorldPos.X || mouseWorldPos.X >= chunkWorldPos.X + sideLength
        || mouseWorldPos.Z < chunkWorldPos.Z || mouseWorldPos.Z >= chunkWorldPos.Z + sideLength)
        {
            return;
        }


        var posInChunk = mouseWorldPos - chunkWorldPos;
        var chunk = pixelsToSet[posInChunk.XInt / 32 + (posInChunk.ZInt / 32) * 3];
        if (chunk == null) return;

        var color = chunk[posInChunk.XInt % 32 + (posInChunk.ZInt % 32) * 32];
        foreach (var item in mapLayer.colorsByBlockCode)
        {
            if (item.Value == color)
            {
                hoverText.Append(Lang.Get(item.Key));
                return;
            }
        }
    }

    public new void setChunk(int dx, int dz, int[] pixels)
    {
        base.setChunk(dx, dz, pixels);

        if (pixelsToSet == null)
        {
            pixelsToSet = new int[9][];
        }

        pixelsToSet[dz * 3 + dx] = pixels;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        map.TranslateWorldPosToViewPos(chunkWorldPos, ref viewPos);
        capi.Render.Render2DTexture(Texture.TextureId, (int)(map.Bounds.renderX + (double)viewPos.X), (int)(map.Bounds.renderY + (double)viewPos.Y), (int)((float)Texture.Width * map.ZoomLevel), (int)((float)Texture.Height * map.ZoomLevel), renderZ, mapLayer.OverlayColor);
    }

    public override void Dispose()
    {
        base.Dispose();
        mapLayer = null;
        pixelsToSet = null;
    }
}