Shader "Custom/LiquidHP"
{
    // Liquid body inside the glass cube. Fragments above the liquid surface are discarded, so on the walls this
    // draws the liquid with its waterline. The surface height comes from LiquidSlosh (fill level, tilt and a wave
    // heightfield texture); the top surface itself is drawn by Custom/LiquidSurface.
    // Without LiquidSlosh (_DrivenFill = 0, e.g. health pickups) the fill follows _HPPercent with a gentle wobble.
    Properties
    {
        _HPPercent       ("HP Percent", Range(0,1)) = 1.0
        _ColorFull       ("Full HP Color", Color) = (0,1,0,1)
        _ColorEmpty      ("Empty HP Color", Color) = (1,0,0,1)
        _FoamColor       ("Foam Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _FoamWidth       ("Foam Width", Range(0.0, 0.12)) = 0.045
        _FoamSoftness    ("Foam Softness", Range(0.001, 0.05)) = 0.015
        _SpecularColor   ("Specular Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _SpecularPower   ("Specular Power", Range(1.0, 128.0)) = 48.0
        _SpecularStrength("Specular Strength", Range(0.0, 1.0)) = 0.45
        _DepthDarkness   ("Liquid Depth Darkness", Range(0.0, 1.0)) = 0.35
        _EmissionStrength("Emission Strength", Range(0.0, 3.0)) = 1.5
        _FillTop         ("Fill Top (HP = 1.0)", Range(-0.5, 0.5)) = 0.4
        _FillBottom      ("Fill Bottom (HP = 0.0)", Range(-0.5, 0.5)) = -0.5
        _FillOffset      ("Fill Offset", Vector) = (0, 0, 0, 0)

        [Header(Undriven fallback (no LiquidSlosh))]
        _WobbleSpeed     ("Wobble Speed", Float) = 2.0
        _WobbleAmount    ("Wobble Amount", Float) = 0.03

        [Header(Set by LiquidSlosh at runtime)]
        [Toggle] _DrivenFill ("Driven By LiquidSlosh", Float) = 0
        _FillLevel       ("Fill Level", Float) = 0.4
        _SurfaceSlope    ("Surface Slope (x, z)", Vector) = (0, 0, 0, 0)
        [NoScaleOffset] _WaveTex ("Wave Heights", 2D) = "black" {}
        _WaveRes         ("Wave Grid Size", Float) = 12
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }

        Pass
        {
            Name "Liquid"
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float  _HPPercent;
            float4 _ColorFull;
            float4 _ColorEmpty;
            float4 _FoamColor;
            float  _FoamWidth;
            float  _FoamSoftness;
            float4 _SpecularColor;
            float  _SpecularPower;
            float  _SpecularStrength;
            float  _DepthDarkness;
            float  _EmissionStrength;
            float  _FillTop;
            float  _FillBottom;
            float4 _FillOffset;
            float  _WobbleSpeed;
            float  _WobbleAmount;
            float  _DrivenFill;
            float  _FillLevel;
            float4 _SurfaceSlope;
            sampler2D _WaveTex;
            float  _WaveRes;

            // URP main light globals (zero outside URP; then a fixed direction is used)
            float4 _MainLightPosition;
            float4 _MainLightColor;

            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f {
                float4 pos         : SV_POSITION;
                float3 localPos    : TEXCOORD0;
                float3 worldPos    : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float3 viewDir     : TEXCOORD3;
            };

            v2f vert(appdata v) {
                v2f o;
                o.pos         = UnityObjectToClipPos(v.vertex);
                o.localPos    = v.vertex.xyz;
                o.worldPos    = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir     = normalize(WorldSpaceViewDir(v.vertex));
                return o;
            }

            float SurfaceHeight(float3 p, float3 worldPos)
            {
                if (_DrivenFill > 0.5)
                {
                    // Grid points sit on texel centres: x = -0.5 -> first texel, x = 0.5 -> last texel
                    float2 uv  = ((p.xz + 0.5) * (_WaveRes - 1.0) + 0.5) / _WaveRes;
                    float wave = tex2Dlod(_WaveTex, float4(uv, 0, 0)).r;
                    return _FillLevel + dot(p.xz, _SurfaceSlope.xy) + wave;
                }

                float wobble = sin(worldPos.x * 6.0 + _Time.y * _WobbleSpeed)
                             * cos(worldPos.z * 4.0 + _Time.y * _WobbleSpeed * 0.7)
                             * _WobbleAmount;
                return lerp(_FillBottom, _FillTop, _HPPercent) + wobble;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 v = normalize(i.viewDir);

                float3 p    = i.localPos - _FillOffset.xyz;
                float  dist = p.y - SurfaceHeight(p, i.worldPos);

                // Above the liquid: let the glass overlay (and the surface mesh) handle it
                if (dist >= 0.0)
                    discard;

                fixed4 baseCol   = lerp(_ColorEmpty, _ColorFull, _HPPercent);
                fixed4 liquidCol = baseCol;

                // Depth darkening
                float depth    = saturate(-dist);
                liquidCol.rgb *= 1.0 - depth * _DepthDarkness;

                // Subsurface brightness near surface
                float nearSurface = saturate(1.0 - (-dist) * 5.0);
                liquidCol.rgb    += liquidCol.rgb * nearSurface * 0.25;

                // Specular highlight from the scene's main light
                float3 lightDir = _MainLightPosition.xyz;
                lightDir = dot(lightDir, lightDir) < 1e-4 ? float3(0.36, 0.89, 0.29) : normalize(lightDir);
                float3 lightCol = dot(_MainLightColor.rgb, _MainLightColor.rgb) < 1e-4 ? float3(1, 1, 1) : _MainLightColor.rgb;
                float3 halfVec  = normalize(lightDir + v);
                float  spec     = pow(saturate(dot(n, halfVec)), _SpecularPower);
                liquidCol.rgb  += _SpecularColor.rgb * lightCol * spec * _SpecularStrength;

                // Meniscus / foam band along the waterline
                float foamDist = saturate((-dist) / (_FoamWidth + _FoamSoftness));
                float foamMask = 1.0 - smoothstep(0.0, 1.0, foamDist);
                liquidCol      = lerp(liquidCol, _FoamColor, foamMask * _FoamColor.a);

                // Emission after everything so foam does not kill the glow
                liquidCol.rgb += baseCol.rgb * _EmissionStrength;

                liquidCol.a = 1.0;
                return liquidCol;
            }
            ENDCG
        }
    }
}
