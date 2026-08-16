#ifndef URP_TRANSPARENT_FOG_INCLUDED
#define URP_TRANSPARENT_FOG_INCLUDED

#include "./AtmosphericScattering.hlsl"

#ifndef SHADERGRAPH_PREVIEW
void PBSFogEvaluateTransparent(float3 positionWS, float2 screenUV, out half3 fogColor, out half3 transmittance)
{
    float3 cameraToFragment = positionWS - GetCameraPositionWS();
    float fragmentDistance = length(cameraToFragment);
    half3 viewDirectionWS = -cameraToFragment * rcp(max(fragmentDistance, FLT_EPS));

    PositionInputs positionInput;
    ZERO_INITIALIZE(PositionInputs, positionInput);
    positionInput.positionWS = positionWS;
    positionInput.positionNDC = screenUV;
    positionInput.positionSS = uint2(screenUV * _ScreenParams.xy);
    // Transparent fragments have a valid raster depth even when ZWrite is disabled. Supplying a
    // non-far value prevents the fullscreen sky fallback from replacing their explicit distance.
    positionInput.deviceDepth = 0.5;
    positionInput.linearDepth = fragmentDistance * dot(-viewDirectionWS, GetViewForwardDir());

    half3 fogOpacity;
    EvaluateAtmosphericScattering(positionInput, viewDirectionWS, fogColor, fogOpacity);
    transmittance = 1.0 - fogOpacity;
}

void PBSFogApplyStraight(
    float3 positionWS,
    float2 screenUV,
    half3 color,
    half alpha,
    out half3 foggedColor,
    out half foggedAlpha)
{
    half3 fogColor;
    half3 transmittance;
    PBSFogEvaluateTransparent(positionWS, screenUV, fogColor, transmittance);
    foggedColor = fogColor + transmittance * color;
    foggedAlpha = alpha;
}

void PBSFogApplyPremultiplied(
    float3 positionWS,
    float2 screenUV,
    half3 premultipliedColor,
    half alpha,
    out half3 foggedColor,
    out half foggedAlpha)
{
    half3 fogColor;
    half3 transmittance;
    PBSFogEvaluateTransparent(positionWS, screenUV, fogColor, transmittance);
    foggedColor = fogColor * alpha + transmittance * premultipliedColor;
    foggedAlpha = alpha;
}

void PBSFogApplyAdditive(
    float3 positionWS,
    float2 screenUV,
    half3 color,
    half alpha,
    out half3 foggedColor,
    out half foggedAlpha)
{
    half3 fogColor;
    half3 transmittance;
    PBSFogEvaluateTransparent(positionWS, screenUV, fogColor, transmittance);
    foggedColor = transmittance * color;
    foggedAlpha = alpha;
}
#endif

// Shader Graph Custom Function wrappers. Use a World-space Position node and the XY output of a
// Default-mode Screen Position node. Shader Graph appends the active precision suffix automatically.
void PBSFogStraight_half(float3 PositionWS, float2 ScreenUV, half3 Color, half Alpha, out half3 FoggedColor, out half FoggedAlpha)
{
#ifdef SHADERGRAPH_PREVIEW
    FoggedColor = Color;
    FoggedAlpha = Alpha;
#else
    PBSFogApplyStraight(PositionWS, ScreenUV, Color, Alpha, FoggedColor, FoggedAlpha);
#endif
}

void PBSFogStraight_float(float3 PositionWS, float2 ScreenUV, float3 Color, float Alpha, out float3 FoggedColor, out float FoggedAlpha)
{
    half3 foggedColor;
    half foggedAlpha;
    PBSFogStraight_half(PositionWS, ScreenUV, Color, Alpha, foggedColor, foggedAlpha);
    FoggedColor = foggedColor;
    FoggedAlpha = foggedAlpha;
}

void PBSFogPremultiplied_half(float3 PositionWS, float2 ScreenUV, half3 Color, half Alpha, out half3 FoggedColor, out half FoggedAlpha)
{
#ifdef SHADERGRAPH_PREVIEW
    FoggedColor = Color;
    FoggedAlpha = Alpha;
#else
    PBSFogApplyPremultiplied(PositionWS, ScreenUV, Color, Alpha, FoggedColor, FoggedAlpha);
#endif
}

void PBSFogPremultiplied_float(float3 PositionWS, float2 ScreenUV, float3 Color, float Alpha, out float3 FoggedColor, out float FoggedAlpha)
{
    half3 foggedColor;
    half foggedAlpha;
    PBSFogPremultiplied_half(PositionWS, ScreenUV, Color, Alpha, foggedColor, foggedAlpha);
    FoggedColor = foggedColor;
    FoggedAlpha = foggedAlpha;
}

void PBSFogAdditive_half(float3 PositionWS, float2 ScreenUV, half3 Color, half Alpha, out half3 FoggedColor, out half FoggedAlpha)
{
#ifdef SHADERGRAPH_PREVIEW
    FoggedColor = Color;
    FoggedAlpha = Alpha;
#else
    PBSFogApplyAdditive(PositionWS, ScreenUV, Color, Alpha, FoggedColor, FoggedAlpha);
#endif
}

void PBSFogAdditive_float(float3 PositionWS, float2 ScreenUV, float3 Color, float Alpha, out float3 FoggedColor, out float FoggedAlpha)
{
    half3 foggedColor;
    half foggedAlpha;
    PBSFogAdditive_half(PositionWS, ScreenUV, Color, Alpha, foggedColor, foggedAlpha);
    FoggedColor = foggedColor;
    FoggedAlpha = foggedAlpha;
}

#endif // URP_TRANSPARENT_FOG_INCLUDED
