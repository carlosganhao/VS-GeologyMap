using System.Collections.Generic;

public class GeologyMapConfig
{
    public float overlayAlpha = 1;
    public Dictionary<string, string> rockCodeColors = new Dictionary<string, string>();
    public HashSet<string> ignoredRocks = new HashSet<string>()
    {
        "game:rock-suevite",
    };
}