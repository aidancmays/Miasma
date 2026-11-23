Shader "Hidden/Miasma"
{
    // Pass 0 marches the fog into a (half resolution) target: rgb = light the fog sends towards the camera,
    // a = how much of what lies behind still shows through. Pass 1 lays it over the frame.
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "Miasma March"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment March
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _MiasmaDensity;   // density, base height, height falloff, noise amount
            float4 _MiasmaNoise;     // 1 / noise size, wind xyz
            float4 _MiasmaLight;     // forward scattering, main light, other lights, ambient
            float4 _MiasmaAlbedo;
            float4 _MiasmaRange;     // distance, steps
            float _MiasmaVolumeCount;
            float4x4 _MiasmaVolumeMatrices[16];
            float4 _MiasmaVolumeParams[16]; // density, edge, noise

            // Value noise, two octaves, so the fog breaks into drifting patches without a texture.
            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float ValueNoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash(i + float3(0, 0, 0)), Hash(i + float3(1, 0, 0)), f.x),
                                 lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x),
                                 lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            float Patches(float3 positionWS)
            {
                float3 p = (positionWS - _MiasmaNoise.yzw * _Time.y) * _MiasmaNoise.x;
                float n = ValueNoise(p) * 0.65 + ValueNoise(p * 2.13 + 17.1) * 0.35;
                return saturate(n * 1.6 - 0.3);
            }

            float Density(float3 positionWS, float patches)
            {
                float height = max(positionWS.y - _MiasmaDensity.y, 0.0);
                float d = _MiasmaDensity.x * exp(-height * _MiasmaDensity.z) * lerp(1.0, patches * 2.0, _MiasmaDensity.w);

                int count = (int)_MiasmaVolumeCount;
                for (int i = 0; i < count; i++)
                {
                    float3 local = mul(_MiasmaVolumeMatrices[i], float4(positionWS, 1.0)).xyz;
                    float3 inside = 0.5 - abs(local); // > 0 inside the unit cube
                    float edge = _MiasmaVolumeParams[i].y;
                    float3 fade = saturate(inside / edge);
                    float w = fade.x * fade.y * fade.z;
                    if (w > 0.0)
                        d += _MiasmaVolumeParams[i].x * w * lerp(1.0, patches * 2.0, _MiasmaVolumeParams[i].z);
                }
                return max(d, 0.0);
            }

            // Per-pixel offset for the first step (interleaved gradient noise).
            float StepOffset(float2 pixel)
            {
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

            float Phase(float cosTheta, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5));
            }

            float4 March(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                float rawDepth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
                #endif
                float3 eye = GetCameraPositionWS();
                float3 end = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 toEnd = end - eye;
                float sceneDistance = length(toEnd);
                float3 dir = toEnd / max(sceneDistance, 1e-4);
                float maxDistance = _MiasmaRange.x;
                float marchDistance = min(sceneDistance, maxDistance);
                if (rawDepth == UNITY_RAW_FAR_CLIP_VALUE) marchDistance = maxDistance; // sky

                int steps = max((int)_MiasmaRange.y, 1);
                float stepLength = marchDistance / steps;
                // Per-pixel offset hides the steps as fine grain instead of bands.
                float jitter = StepOffset(input.positionCS.xy);

                Light mainLight = GetMainLight();
                float mainPhase = Phase(dot(dir, mainLight.direction), _MiasmaLight.x);
                float3 mainColour = mainLight.color * _MiasmaLight.y;
                float3 ambient = (SampleSH(float3(0, 1, 0)) + SampleSH(float3(0, -1, 0))) * 0.5 * _MiasmaLight.w;
                float3 albedo = _MiasmaAlbedo.rgb;

                InputData inputData = (InputData)0;
                inputData.normalizedScreenSpaceUV = uv;

                float3 scattered = 0.0;
                float transmittance = 1.0;
                [loop]
                for (int s = 0; s < steps; s++)
                {
                    float t = (s + jitter) * stepLength;
                    float3 p = eye + dir * t;
                    float patches = Patches(p);
                    float extinction = Density(p, patches);
                    if (extinction <= 1e-6) continue;

                    float3 light = ambient;

                    float shadow = 1.0;
                    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                    #endif
                    light += mainColour * shadow * mainPhase * 4.0 * PI;

                    #if defined(_ADDITIONAL_LIGHTS)
                    UNITY_BRANCH if (_MiasmaLight.z > 0.0)
                    {
                        inputData.positionWS = p;
                        uint lightCount = GetAdditionalLightsCount();
                        LIGHT_LOOP_BEGIN(lightCount)
                            Light other = GetAdditionalLight(lightIndex, p, half4(1, 1, 1, 1));
                            float otherPhase = Phase(dot(dir, other.direction), _MiasmaLight.x) * 4.0 * PI;
                            light += other.color * other.distanceAttenuation * other.shadowAttenuation * otherPhase * _MiasmaLight.z;
                        LIGHT_LOOP_END
                    }
                    #endif

                    // Energy-conserving step: light scattered within the step, dimmed by the fog in front of it.
                    float stepTransmittance = exp(-extinction * stepLength);
                    float3 inscatter = light * albedo * (1.0 - stepTransmittance);
                    scattered += transmittance * inscatter;
                    transmittance *= stepTransmittance;
                    if (transmittance < 0.003) { transmittance = 0.0; break; }
                }

                return float4(scattered, transmittance);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Miasma Composite"
            // result = fog light + what was there * how much shows through
            Blend One SrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_MiasmaFogTexture);

            float4 Composite(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D_LOD(_MiasmaFogTexture, sampler_LinearClamp, input.texcoord, 0);
            }
            ENDHLSL
        }
    }
}
