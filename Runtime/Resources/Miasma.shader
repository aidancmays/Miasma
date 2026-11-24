Shader "Hidden/Miasma"
{
    // Pass 3 (Marched): marches each (half resolution) pixel's view ray through the fog, gathering the light it scatters
    // towards the camera, into its own texture; passes 0 and 1 then read that instead of the froxels.
    // Pass 0 (In Scene): lays the froxel fog over the frame. Each pixel looks up the light gathered and the fraction of
    // the background left at its distance; beyond the grid the height fog carries on (worked out exactly, without noise
    // or shadows), so distant things and the sky keep fading into the fog.
    // Pass 1 (Painted, first half): the same fog, written to its own layer (rgb = fog light, a = cover) instead.
    // Pass 2 (Painted, second half): paints that layer over the finished frame as watercolour washes.
    // Passes 0 and 1 also write how much of each pixel shows through (for a print to fade its detail by).
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE3D(_MiasmaIntegratedRead);
        TEXTURE2D(_MiasmaLayer);
        TEXTURE2D(_MiasmaMarched);

        float4 _MiasmaGrid;          // width, height, depth
        float4 _MiasmaSlices;        // near, far, log(far / near)
        float4 _MiasmaDensity;       // density, base height, height falloff
        float4 _MiasmaLight;         // forward scattering, main light, other lights, ambient
        float4 _MiasmaAlbedo;
        float4 _MiasmaMainDirection;
        float4 _MiasmaMainColour;
        float4 _MiasmaAmbient;
        float4 _MiasmaPaint;         // wash steps, blooms, granulation, wet edges
        float4 _MiasmaPaint2;        // pigment size, colour exposure, layer texel size xy
        float4 _MiasmaPigment;       // rgb tint, a = how much of it
        float4 _MiasmaMorph;         // bloom morph phase (in shapes), unused
        float4 _MiasmaMethod;        // x = 1 marched, 0 froxels; zw = marched texel size
        float4 _MiasmaNoise;         // 1 / noise size, wind xyz
        float4 _MiasmaRange;         // distance, steps, frame
        float _MiasmaVolumeCount;
        float4x4 _MiasmaVolumeMatrices[16];
        float4 _MiasmaVolumeParams[16]; // density, edge, noise

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
            return lerp(lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), f.x),
                             lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                        lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x),
                             lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y), f.z);
        }

        // Fog per metre at a point: height fog broken into drifting patches, plus the fog volumes.
        float Density(float3 p)
        {
            float3 q = (p - _MiasmaNoise.yzw * _Time.y) * _MiasmaNoise.x;
            float patches = saturate((ValueNoise(q) * 0.65 + ValueNoise(q * 2.13 + 17.1) * 0.35) * 1.6 - 0.3) * 2.0;
            float height = max(p.y - _MiasmaDensity.y, 0.0);
            float d = _MiasmaDensity.x * exp(-height * _MiasmaDensity.z) * lerp(1.0, patches, _MiasmaDensity.w);
            int count = (int)_MiasmaVolumeCount;
            for (int i = 0; i < count; i++)
            {
                float3 local = mul(_MiasmaVolumeMatrices[i], float4(p, 1.0)).xyz;
                float3 fade = saturate((0.5 - abs(local)) / _MiasmaVolumeParams[i].y);
                d += _MiasmaVolumeParams[i].x * fade.x * fade.y * fade.z * lerp(1.0, patches, _MiasmaVolumeParams[i].z);
            }
            return max(d, 0.0);
        }

        // The marched fog, softened over a few of its (half resolution) pixels so the step grain never shows.
        float4 MarchedAt(float2 uv)
        {
            float2 t = _MiasmaMethod.zw;
            float4 sum = SAMPLE_TEXTURE2D_LOD(_MiasmaMarched, sampler_LinearClamp, uv, 0) * 2.0;
            sum += SAMPLE_TEXTURE2D_LOD(_MiasmaMarched, sampler_LinearClamp, uv + t * float2(1.0, 0.5), 0);
            sum += SAMPLE_TEXTURE2D_LOD(_MiasmaMarched, sampler_LinearClamp, uv + t * float2(-0.5, 1.0), 0);
            sum += SAMPLE_TEXTURE2D_LOD(_MiasmaMarched, sampler_LinearClamp, uv + t * float2(-1.0, -0.5), 0);
            sum += SAMPLE_TEXTURE2D_LOD(_MiasmaMarched, sampler_LinearClamp, uv + t * float2(0.5, -1.0), 0);
            return sum / 6.0;
        }

        float Phase(float cosTheta, float g)
        {
            float g2 = g * g;
            return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5);
        }

        // Optical depth of the height fog along a straight path (no noise).
        float HeightFogDepth(float3 start, float3 dir, float pathLength)
        {
            float k = _MiasmaDensity.z;
            float h = max(start.y - _MiasmaDensity.y, 0.0);
            float base = _MiasmaDensity.x * exp(-h * k);
            float rise = k * dir.y * pathLength;
            float factor = abs(rise) > 1e-4 ? (1.0 - exp(-rise)) / rise : 1.0;
            return min(base * pathLength * factor, _MiasmaDensity.x * pathLength);
        }

        // rgb = fog light reaching the camera, a = how much of what lies behind shows through.
        float4 FogAt(float2 uv)
        {
            if (_MiasmaMethod.x > 0.5) return MarchedAt(uv);
            float rawDepth = SampleSceneDepth(uv);
            #if !UNITY_REVERSED_Z
            rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
            #endif
            bool sky = rawDepth == UNITY_RAW_FAR_CLIP_VALUE;
            float3 eye = GetCameraPositionWS();
            float3 world = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
            float3 toWorld = world - eye;
            float d = length(toWorld);
            float3 dir = toWorld / max(d, 1e-4);
            float far = _MiasmaSlices.y;
            if (sky) d = far + 20000.0;

            float s = log(max(min(d, far), _MiasmaSlices.x) / _MiasmaSlices.x) / _MiasmaSlices.z * _MiasmaGrid.z;
            float w = clamp(s - 0.5, 0.5, _MiasmaGrid.z - 0.5) / _MiasmaGrid.z;
            // Four taps half a froxel apart (rotated grid) soften the grid's cells into the fog.
            float2 cell = 0.5 / _MiasmaGrid.xy;
            float4 fog = (SAMPLE_TEXTURE3D_LOD(_MiasmaIntegratedRead, sampler_LinearClamp, float3(uv + cell * float2(0.25, 0.75), w), 0)
                        + SAMPLE_TEXTURE3D_LOD(_MiasmaIntegratedRead, sampler_LinearClamp, float3(uv + cell * float2(-0.75, 0.25), w), 0)
                        + SAMPLE_TEXTURE3D_LOD(_MiasmaIntegratedRead, sampler_LinearClamp, float3(uv + cell * float2(-0.25, -0.75), w), 0)
                        + SAMPLE_TEXTURE3D_LOD(_MiasmaIntegratedRead, sampler_LinearClamp, float3(uv + cell * float2(0.75, -0.25), w), 0)) * 0.25;
            if (s < 1.0) fog = lerp(float4(0, 0, 0, 1), fog, saturate(s));

            if (d > far)
            {
                float depth = HeightFogDepth(eye + dir * far, dir, d - far);
                float beyond = exp(-depth);
                float3 light = _MiasmaAmbient.rgb * _MiasmaLight.w
                    + _MiasmaMainColour.rgb * Phase(dot(dir, _MiasmaMainDirection.xyz), _MiasmaLight.x) * _MiasmaLight.y;
                fog.rgb += fog.a * light * _MiasmaAlbedo.rgb * (1.0 - beyond);
                fog.a *= beyond;
            }
            return fog;
        }

        struct FogOutput
        {
            float4 target : SV_Target0;
            float visibility : SV_Target1;
        };
        ENDHLSL

        Pass
        {
            Name "Miasma In Scene"
            // Target 0: result = fog light + what was there * how much shows through. Target 1: how much shows through.
            Blend 0 One SrcAlpha
            Blend 1 Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment InScene

            FogOutput InScene(Varyings input)
            {
                float4 fog = FogAt(input.texcoord);
                FogOutput o;
                o.target = fog;
                o.visibility = fog.a;
                return o;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Miasma Layer"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Layer

            FogOutput Layer(Varyings input)
            {
                float4 fog = FogAt(input.texcoord);
                FogOutput o;
                o.target = float4(fog.rgb, 1.0 - fog.a);
                o.visibility = fog.a;
                return o;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Miasma Paint"
            // Premultiplied: result = paint + what was there * (1 - cover).
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Paint

            float Fbm(float3 p)
            {
                return ValueNoise(p) * 0.55 + ValueNoise(p * 2.07 + 11.3) * 0.3 + ValueNoise(p * 4.13 + 5.7) * 0.15;
            }

            // Noise at one power-of-two step: the same function for a given step wherever it is used.
            float3 StepPoint(float3 pos, float stepIndex, float cell)
            {
                return pos / (exp2(stepIndex) * cell) + frac(stepIndex * float3(0.6180339, 0.4142135, 0.7320508)) * 97.0;
            }

            float GrainAt(float3 p)
            {
                return ValueNoise(p) * 0.6 + ValueNoise(p * 2.03 + 3.1) * 0.4;
            }

            // Blooms that change shape in place, never sliding: each shape is a field with its own seed, and the
            // blooms fade from one shape to the next, with the contrast held so they don't flatten mid-fade.
            float BloomAt(float3 p)
            {
                float phase = _MiasmaMorph.x;
                float k = floor(phase);
                float t = smoothstep(0.0, 1.0, phase - k);
                float a = Fbm(p + frac(k * float3(0.3819660, 0.7548777, 0.5698403)) * 211.0);
                float b = Fbm(p + frac((k + 1.0) * float3(0.3819660, 0.7548777, 0.5698403)) * 211.0);
                float v = lerp(a, b, t) - 0.5;
                return 0.5 + v * rsqrt((1.0 - t) * (1.0 - t) + t * t);
            }

            float CoverAt(float2 uv) { return SAMPLE_TEXTURE2D_LOD(_MiasmaLayer, sampler_LinearClamp, uv, 0).a; }

            float4 Paint(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 layer = SAMPLE_TEXTURE2D_LOD(_MiasmaLayer, sampler_LinearClamp, uv, 0);
                float cover = layer.a;
                if (cover <= 0.002) return 0.0;

                // The fog's own colour (its light per unit of cover), brought down to the frame's range.
                float3 colour = layer.rgb / max(cover, 1e-3) * _MiasmaPaint2.y;
                colour = colour / (1.0 + max(colour.r, max(colour.g, colour.b)));
                colour = lerp(colour, _MiasmaPigment.rgb * Luminance(colour) * 2.0, _MiasmaPigment.a);

                // The paper the washes sit in, fixed in space like the print's: mapped onto whatever lies behind the
                // fog (or, for the sky, a far-off sphere by direction), scaled in powers of two with distance so the
                // grain keeps about the same size on screen, the two nearest scales blended.
                float rawDepth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
                #endif
                float3 eye = GetCameraPositionWS();
                float3 world = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float d = length(world - eye);
                float3 pos = world;
                if (rawDepth == UNITY_RAW_FAR_CLIP_VALUE)
                {
                    d = 1000.0;
                    pos = (world - eye) / max(length(world - eye), 1e-4) * d;
                }
                // World size of one 1080p pixel at this distance.
                float pixel = d * 2.0 / (abs(UNITY_MATRIX_P._m11) * 1080.0);
                float level = log2(max(pixel, 1e-5));
                float step0 = floor(level);
                float blend = level - step0;
                float size = _MiasmaPaint2.x;
                // In pixels at 1080p: blooms about 140 across, grain about 3.
                float bloom = lerp(BloomAt(StepPoint(pos, step0, 140.0 * size)), BloomAt(StepPoint(pos, step0 + 1.0, 140.0 * size)), blend);
                float grain = lerp(GrainAt(StepPoint(pos, step0, 3.0 * size)), GrainAt(StepPoint(pos, step0 + 1.0, 3.0 * size)), blend);

                // The washes are layered like glazes: every effect below changes how much pigment (optical depth) is
                // laid, never knocks holes in it, so thick fog still builds up to a solid, unbroken wash.
                float depth = -log(max(1.0 - cover, 1e-4));

                // Blooms: pigment gathers in patches, a cauliflower edge where the wash spread on wet paper.
                depth *= exp2((bloom - 0.5) * 2.0 * _MiasmaPaint.y);

                // Steps: the fog laid as a few overlapping washes, each with a soft edge.
                // Steps: each wash lays about the same amount of pigment, with a soft edge that wanders with the blooms.
                float layerDepth = lerp(0.08, 0.7, _MiasmaPaint.x);
                float band = depth / layerDepth + (bloom - 0.5) * 1.5;
                float stepped = max((floor(band) + smoothstep(0.3, 0.7, frac(band)) - (bloom - 0.5) * 1.5) * layerDepth, 0.0);
                depth = lerp(depth, stepped, _MiasmaPaint.x);

                // Wet edges: where a wash ends, its pigment dried darker.
                float2 texel = _MiasmaPaint2.zw * 4.0;
                float edge = abs(CoverAt(uv + float2(texel.x, 0)) - CoverAt(uv - float2(texel.x, 0)))
                           + abs(CoverAt(uv + float2(0, texel.y)) - CoverAt(uv - float2(0, texel.y)));
                edge = saturate(edge * 6.0) * _MiasmaPaint.w;
                depth += edge * 0.3;
                colour *= 1.0 - edge * 0.35;

                // Granulation: pigment settles into the paper's tooth.
                float settle = (grain - 0.5) * _MiasmaPaint.z;
                depth *= 1.0 + settle * 0.8;
                colour *= 1.0 - max(settle, 0.0) * 0.4;

                float washed = 1.0 - exp(-depth);

                return float4(colour * washed, washed);
            }
            ENDHLSL
        }
        Pass
        {
            Name "Miasma March"
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment March
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // A different spot along each ray every frame (interleaved gradient noise), so the steps show as fine grain
            // that moves instead of a fixed pattern.
            float StepOffset(float2 pixel, float frame)
            {
                pixel += 5.588238 * fmod(frame, 64.0);
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

            float4 March(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float rawDepth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
                #endif
                bool sky = rawDepth == UNITY_RAW_FAR_CLIP_VALUE;
                float3 eye = GetCameraPositionWS();
                float3 end = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float sceneDistance = length(end - eye);
                float3 dir = (end - eye) / max(sceneDistance, 1e-4);
                float far = _MiasmaRange.x;
                if (sky) sceneDistance = far + 20000.0;
                float marchDistance = min(sceneDistance, far);

                int steps = max((int)_MiasmaRange.y, 1);
                float stepLength = marchDistance / steps;
                float jitter = StepOffset(input.positionCS.xy, _MiasmaRange.z);

                Light mainLight = GetMainLight();
                float3 mainColour = mainLight.color * Phase(dot(dir, mainLight.direction), _MiasmaLight.x) * _MiasmaLight.y;
                float3 ambient = _MiasmaAmbient.rgb * _MiasmaLight.w;

                InputData inputData = (InputData)0;
                inputData.normalizedScreenSpaceUV = uv;

                float3 gathered = 0.0;
                float transmittance = 1.0;
                [loop]
                for (int i = 0; i < steps; i++)
                {
                    float3 p = eye + dir * ((i + jitter) * stepLength);
                    float extinction = Density(p);
                    if (extinction <= 1e-6) continue;

                    float3 light = ambient;
                    float shadow = 1.0;
                    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                    #endif
                    light += mainColour * shadow;

                    #if defined(_ADDITIONAL_LIGHTS)
                    UNITY_BRANCH if (_MiasmaLight.z > 0.0)
                    {
                        inputData.positionWS = p;
                        uint lightCount = GetAdditionalLightsCount();
                        LIGHT_LOOP_BEGIN(lightCount)
                            Light other = GetAdditionalLight(lightIndex, p, half4(1, 1, 1, 1));
                            // Even glow, and soft near the bulb (no blinding peak there).
                            float a = other.distanceAttenuation * other.shadowAttenuation;
                            light += other.color * (a / (1.0 + a)) * _MiasmaLight.z;
                        LIGHT_LOOP_END
                    }
                    #endif

                    float stepTransmittance = exp(-extinction * stepLength);
                    gathered += transmittance * light * _MiasmaAlbedo.rgb * (1.0 - stepTransmittance);
                    transmittance *= stepTransmittance;
                    if (transmittance < 0.002) { transmittance = 0.0; break; }
                }

                // Beyond the marched distance the height fog carries on, so far things and the sky keep fading out.
                if (sceneDistance > far && transmittance > 0.0)
                {
                    float depth = HeightFogDepth(eye + dir * far, dir, sceneDistance - far);
                    float beyond = exp(-depth);
                    float3 light = ambient + _MiasmaMainColour.rgb * Phase(dot(dir, _MiasmaMainDirection.xyz), _MiasmaLight.x) * _MiasmaLight.y;
                    gathered += transmittance * light * _MiasmaAlbedo.rgb * (1.0 - beyond);
                    transmittance *= beyond;
                }
                return float4(gathered, transmittance);
            }
            ENDHLSL
        }
    }
}
