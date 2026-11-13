using UnityEngine;

namespace Stellar.EntityInspector;

/// <summary>A map-art base layer the Location tab can crop + mark: a bottom-origin RGBA32 buffer and a world → buffer-pixel
/// projection. Two sources: <see cref="LocationMapSource"/> (the live radar's art, our own scene, projected by the game's
/// <c>GetImgPos</c>) and <see cref="SceneMapLoader"/> (any scene's art loaded from its assets, projected from its
/// <c>ZMapRegionData</c>).</summary>
internal interface ILocationBase
{
    byte[]? Base { get; }
    int BaseW { get; }
    int BaseH { get; }
    /// <summary>Base-buffer px per world unit (crop sizing).</summary>
    float PxPerWorld { get; }
    /// <summary>Bumps on every (re)build so the painter redraws.</summary>
    int Version { get; }
    /// <summary>World → base-buffer pixel (bottom-left origin, world X → u, Z → v, Y ignored).</summary>
    bool TryImgPos(Vector3 world, out Vector2 px);
}
