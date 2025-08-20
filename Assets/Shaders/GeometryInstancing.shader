Shader "RayTracing/MeshInstancing"
{
    Properties
    {
        _Color("Main Color", Color) = (1, 1, 1, 1)
        _MainTex("Albedo (RGB)", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }
        LOD 100

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag() : SV_Target
            {
                return fixed4(1, 1, 1, 1);
            }

            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }

        Pass
        {
            // RayTracingShader.SetShaderPass must use this name in order to execute the ray tracing shaders from this Pass.
            Name "Test1"

            HLSLPROGRAM

            #pragma multi_compile _ INSTANCING_ON

            #pragma enable_ray_tracing_shader_debug_symbols

            // Specify this shader is a raytracing shader. The name is not important.
            #pragma raytracing test

            struct AttributeData
            {
                float2 barycentrics;
            };

            struct RayPayload
            {
                float3 color;
            };

            // Set by Unity.
            uint unity_BaseInstanceID;

            StructuredBuffer<float3> InstanceColors;

            float3 _Color;

            [shader("closesthit")]
            void ClosestHitMain(inout RayPayload payload, AttributeData attribs)
            {
    #if INSTANCING_ON
                uint instanceIndex = InstanceIndex() - unity_BaseInstanceID;
                payload.color = InstanceColors[instanceIndex];
    #else
                payload.color = _Color;
    #endif
            }

            ENDHLSL
        }
    }

    SubShader
    {
        Pass
        {
            Name "Test"

            HLSLPROGRAM

            #include "UnityRayTracingMeshUtils.cginc"
            #include "RayPayload.hlsl"
            #include "GlobalResources.hlsl"

            #pragma raytracing some_name

            float3 _Color;

            // Use INSTANCING_ON shader keyword for supporting instanced and non-instanced geometries.
            // Unity will setup SH coeffiecients - unity_SHAArray, unity_SHBArray, etc when RayTracingAccelerationStructure.AddInstances is used.
            #pragma multi_compile _ INSTANCING_ON

            Texture2D<float4> _MainTex;
            SamplerState sampler__MainTex;
            float4 _MainTex_ST;

            float4 _WorldSpaceLightPos0;

            struct AttributeData
            {
                float2 barycentrics;
            };

            struct Vertex
            {
                float3 normal;
                float2 uv;
            };

            Vertex FetchVertex(uint vertexIndex)
            {
                Vertex v;
                v.normal = UnityRayTracingFetchVertexAttribute3(vertexIndex, kVertexAttributeNormal);
                v.uv = UnityRayTracingFetchVertexAttribute2(vertexIndex, kVertexAttributeTexCoord0);
                return v;
            }

            Vertex InterpolateVertices(Vertex v0, Vertex v1, Vertex v2, float3 barycentrics)
            {
                Vertex v;
#define INTERPOLATE_ATTRIBUTE(attr) v.attr = v0.attr * barycentrics.x + v1.attr * barycentrics.y + v2.attr * barycentrics.z
                INTERPOLATE_ATTRIBUTE(normal);
                INTERPOLATE_ATTRIBUTE(uv);
                return v;
            }

            [shader("closesthit")]
            void ClosestHitMain(inout RayPayload payload : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                uint3 triangleIndices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());

                Vertex v0, v1, v2;
                v0 = FetchVertex(triangleIndices.x);
                v1 = FetchVertex(triangleIndices.y);
                v2 = FetchVertex(triangleIndices.z);

                float3 barycentricCoords = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                Vertex v = InterpolateVertices(v0, v1, v2, barycentricCoords);

                float3 worldNormal = normalize(mul(v.normal, (float3x3)WorldToObject()));

                float3 lightDir = _WorldSpaceLightPos0.xyz;
                float light1 = saturate(dot(worldNormal, normalize(lightDir)));
                float light2 = saturate(dot(worldNormal, normalize(-lightDir)));

                payload.color = saturate(light1 + light2) * _Color.xyz * _MainTex.SampleLevel(sampler__MainTex, _MainTex_ST.xy * v.uv + _MainTex_ST.zw, 0).xyz;
            }

            ENDHLSL
        }
    }
}
