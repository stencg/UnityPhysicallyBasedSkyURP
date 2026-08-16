# Documentation

This page will be available soon.

You may also refer to [HDRP's Physically Based Sky documentation](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/create-a-physically-based-sky.html).

## Transparent fog

Transparent shaders must evaluate fog from their own fragment position because they normally do not
write depth. Include `Packages/com.jiaozi158.unity-physically-based-sky-urp/Shaders/TransparentFog.hlsl`
and use the function matching the material's blend mode:

- `PBSFogApplyStraight` for `Blend SrcAlpha OneMinusSrcAlpha`.
- `PBSFogApplyPremultiplied` for `Blend One OneMinusSrcAlpha`. Its input color must already be
  multiplied by alpha.
- `PBSFogApplyAdditive` for additive particles. It attenuates emission without adding fog color a
  second time.

Each function takes world-space position, normalized screen UV, color, and alpha, and returns fogged
color and unchanged alpha.

For Shader Graph, create a **Custom Function** node in **File** mode:

1. Set **Source** to `TransparentFog.hlsl`.
2. Set **Name** to `PBSFogStraight`, `PBSFogPremultiplied`, or `PBSFogAdditive`.
3. Connect a **Position** node in **World** space to `PositionWS`.
4. Connect XY from a **Screen Position** node in **Default** mode to `ScreenUV`.
5. Connect the graph color and alpha to `Color` and `Alpha`, then use `FoggedColor` and
   `FoggedAlpha` as the graph outputs.

Evaluate these functions in the fragment stage. Per-vertex evaluation is cheaper but can produce
incorrect gradients across large particles.
