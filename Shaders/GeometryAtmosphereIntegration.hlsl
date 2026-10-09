#ifndef PBSKY_GEOMETRY_INTEGRATION_INCLUDED
#define PBSKY_GEOMETRY_INTEGRATION_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "./PhysicallyBasedSkyRendering.hlsl"
#include "./PhysicallyBasedSkyEvaluation.hlsl"

#define PBSKY_GEOMETRY_WIDTH 32
#define PBSKY_GEOMETRY_DEPTH 64
#define PBSKY_GEOMETRY_DISTANCE 128000.0
float4x4 _PBSkyGeometryInvVP[2];
float4 _PBSkyGeometryCameraWS[2];
float4 _PBSkyGeometryOriginPS[2];
float4 _PBSkyGeometryUpRadius[2];
int _PBSkyGeometryEyeCount;
int _PBSkyGeometryCameraSpace;
int _PBSkyGeometryEye;
int _PBSkyGeometrySlice;

float PBSkyGeometryDistance(uint slice)
{
    float s = slice / (float)(PBSKY_GEOMETRY_DEPTH - 1);
    return s * s * PBSKY_GEOMETRY_DISTANCE;
}

void PBSkyGeometryRay(float2 uv, uint eye, out float3 O, out float3 V, out float entry, out float exitDistance)
{
    float3 farWS = ComputeWorldSpacePosition(uv, UNITY_RAW_FAR_CLIP_VALUE, _PBSkyGeometryInvVP[eye]);
    V = normalize(farWS - _PBSkyGeometryCameraWS[eye].xyz);
    O = _PBSkyGeometryOriginPS[eye].xyz;
    if (_PBSkyGeometryCameraSpace != 0)
    {
        V.y = max(V.y, 0.0);
        V = dot(V,V) > 1e-8 ? normalize(V) : float3(0,0,1);
    }
    float r = _PBSkyGeometryUpRadius[eye].w;
    float2 bounds = IntersectSphere(_AtmosphericRadius, dot(_PBSkyGeometryUpRadius[eye].xyz, V), r);
    entry = max(bounds.x, 0.0);
    exitDistance = bounds.y < 0.0 ? 0.0 : max(bounds.y - entry, 0.0);
    O += entry * V;
}

// Both generators integrate the same medium and use incoming, not outgoing, transmittance.
void PBSkyGeometryStep(float3 O, float3 V, float t0, float t1, inout float3 radiance, inout float3 transmission)
{
    float dt = max(t1 - t0, 0.0);
    if (dt <= 0.0) return;
    float t = (t0 + t1) * 0.5;
    float3 P = O + t * V;
    float originRadius = length(O);
    float radius = length(P);
    // Use the difference of squared radii for altitude, retaining small ray offsets
    // instead of subtracting two Earth-sized sample coordinates.
    float height = max((originRadius - _PlanetaryRadius) +
        (2.0 * dot(O, V) * t + t * t) / max(radius + originRadius, 1.0), 1.0);
    float3 N = P / max(radius, 1.0);
    float r = max(radius, _PlanetaryRadius + 1.0);
    float3 lightingDirection = V;
    if (_PBSkyGeometryCameraSpace == 0 && radius < _PlanetaryRadius)
    {
        P = N * _PlanetaryRadius;
        float3 projectedDirection = P - O;
        if (dot(projectedDirection, projectedDirection) > 1e-8)
            lightingDirection = normalize(projectedDirection);
    }

    float3 extinction = AtmosphereExtinction(height);
    float3 air = AirScatter(height);
    float3 aerosol = AerosolScatter(height);
    CelestialBodyData light = GetCelestialBody();
    float3 L = -light.forward;
    float3 source = light.color * (EvaluateSunColorAttenuation(dot(N, L), r) *
        (air * AirPhase(-dot(L, lightingDirection)) + aerosol * AerosolPhase(-dot(L, lightingDirection))) +
        EvaluateMultipleScattering(dot(N, L), height) * (air + aerosol));
    float3 opticalDepth = extinction * dt;
    float3 segmentTransmission = exp(-opticalDepth);
    // Avoid cancellation and division by nearly zero extinction for short/empty segments.
    float3 integral = float3(
        opticalDepth.x < 1e-3 ? dt * (1.0 - 0.5 * opticalDepth.x) : (1.0 - segmentTransmission.x) / extinction.x,
        opticalDepth.y < 1e-3 ? dt * (1.0 - 0.5 * opticalDepth.y) : (1.0 - segmentTransmission.y) / extinction.y,
        opticalDepth.z < 1e-3 ? dt * (1.0 - 0.5 * opticalDepth.z) : (1.0 - segmentTransmission.z) / extinction.z);
    radiance += transmission * source * integral;
    transmission *= segmentTransmission;
}
#endif
