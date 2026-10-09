Shader "Hidden/Sky/PBSkyGeometryRaster"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #include "../GeometryAtmosphereIntegration.hlsl"
        TEXTURE2D(_PBSkyGeometryPreviousRadiance);
        TEXTURE2D(_PBSkyGeometryPreviousTransmission);
        TEXTURE3D(_PBSkyGeometryRadianceSource);
        struct Varyings { float4 positionCS : SV_POSITION; };
        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings o; o.positionCS = GetFullScreenTriangleVertexPosition(vertexID); return o;
        }
        void Integrate(Varyings input, out float4 radiance : SV_Target0, out float4 transmission : SV_Target1)
        {
            int2 pixel = int2(input.positionCS.xy);
            radiance = float4(0,0,0,1); transmission = 1.0;
            if (_PBSkyGeometrySlice == 0) return;
            radiance = LOAD_TEXTURE2D(_PBSkyGeometryPreviousRadiance, pixel);
            transmission = LOAD_TEXTURE2D(_PBSkyGeometryPreviousTransmission, pixel);
            uint eye = (uint)pixel.x >> 5;
            float2 uv = (float2(((uint)pixel.x & 31u), pixel.y) + 0.5) / PBSKY_GEOMETRY_WIDTH;
            float3 O, V; float entry, exitDistance;
            PBSkyGeometryRay(uv, eye, O, V, entry, exitDistance);
            float3 color = radiance.rgb, tr = transmission.rgb;
            PBSkyGeometryStep(O, V, min(PBSkyGeometryDistance(_PBSkyGeometrySlice-1), exitDistance),
                min(PBSkyGeometryDistance(_PBSkyGeometrySlice), exitDistance), color, tr);
            radiance.rgb = color; transmission.rgb = tr;
        }
        float4 Filter(Varyings input) : SV_Target
        {
            int2 pixel = int2(input.positionCS.xy);
            int eyeStart = pixel.x & ~31;
            float4 sum = 0;
            for (int y=-1; y<=1; y++)
            for (int x=-1; x<=1; x++)
            {
                int2 p = clamp(pixel+int2(x,y), int2(eyeStart,0), int2(eyeStart+31,31));
                sum += LOAD_TEXTURE3D_LOD(_PBSkyGeometryRadianceSource, int3(p,_PBSkyGeometrySlice),0) *
                    (x == 0 ? 2.0 : 1.0) * (y == 0 ? 2.0 : 1.0);
            }
            return sum / 16.0;
        }
        ENDHLSL
        Pass
        {
            Name "Integrate"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Integrate
            ENDHLSL
        }
        Pass
        {
            Name "Filter"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Filter
            ENDHLSL
        }
    }
}
