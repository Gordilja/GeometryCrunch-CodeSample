Shader "Custom/LiquidSurface"
{
    // Top surface of the cube liquid: a grid mesh moved by LiquidSlosh (tilt + ripples). Uses the same colours and
    // glow as Custom/LiquidHP so walls and surface read as one liquid; ripples show through light, highlight and rim.
    Properties
    {
        _HPPercent        ("HP Percent (set by LiquidSlosh)", Range(0,1)) = 1.0
        _ColorFull        ("Full HP Color (set by LiquidSlosh)", Color) = (0,1,0,1)
        _ColorEmpty       ("Empty HP Color (set by LiquidSlosh)", Color) = (1,0,0,1)
        _SurfaceLift      ("Surface Brightness", Range(0.0, 1.0)) = 0.3
        _Shading          ("Light Shading (shows ripples)", Range(0.0, 1.0)) = 0.55
        _EmissionStrength ("Emission Strength", Range(0.0, 3.0)) = 1.3
        _SpecularPower    ("Specular Power", Range(1.0, 256.0)) = 90.0
        _SpecularStrength ("Specular Strength", Range(0.0, 2.0)) = 0.9
        _FresnelPower     ("Fresnel Power", Range(0.5, 8.0)) = 3.0
        _FresnelStrength  ("Fresnel Strength", Range(0.0, 1.0)) = 0.35
        _EdgeColor        ("Meniscus Color", Color) = (1.0, 1.0, 1.0, 0.8)
        _EdgeWidth        ("Meniscus Width (uv)", Range(0.0, 0.2)) = 0.04
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }

        Pass
        {
            Name "LiquidSurface"
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float  _HPPercent;
            float4 _ColorFull;
            float4 _ColorEmpty;
            float  _SurfaceLift;
            float  _Shading;
            float  _EmissionStrength;
            float  _SpecularPower;
            float  _SpecularStrength;
            float  _FresnelPower;
            float  _FresnelStrength;
            float4 _EdgeColor;
            float  _EdgeWidth;

            float4 _MainLightPosition;
            float4 _MainLightColor;

            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct v2f {
                float4 pos         : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 viewDir     : TEXCOORD1;
                float2 uv          : TEXCOORD2;
            };

            v2f vert(appdata v) {
                v2f o;
                o.pos         = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir     = normalize(WorldSpaceViewDir(v.vertex));
                o.uv          = v.uv;
                return o;
            }

            fixed4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.worldNormal) * (facing > 0 ? 1.0 : -1.0);
                float3 v = normalize(i.viewDir);

                float3 lightDir = _MainLightPosition.xyz;
                lightDir = dot(lightDir, lightDir) < 1e-4 ? float3(0.36, 0.89, 0.29) : normalize(lightDir);
                float3 lightCol = dot(_MainLightColor.rgb, _MainLightColor.rgb) < 1e-4 ? float3(1, 1, 1) : _MainLightColor.rgb;

                fixed3 baseCol = lerp(_ColorEmpty, _ColorFull, _HPPercent).rgb;
                fixed3 col     = baseCol * (1.0 + _SurfaceLift);

                // Light across the ripples: slopes facing the sun brighten, slopes facing away darken
                float ndl = dot(n, lightDir);
                col *= lerp(1.0, 0.55 + 0.45 * saturate(ndl * 0.5 + 0.5) * 1.6, _Shading);

                // Sun glint on the ripples
                float3 h   = normalize(lightDir + v);
                float spec = pow(saturate(dot(n, h)), _SpecularPower);
                col += lightCol * spec * _SpecularStrength;

                // Grazing angles pick up a lighter sheen
                float fres = pow(1.0 - saturate(dot(n, v)), _FresnelPower);
                col = lerp(col, col + baseCol * 0.6 + 0.15, fres * _FresnelStrength);

                // Meniscus: bright rim where the liquid touches the glass
                float edge     = min(min(i.uv.x, 1.0 - i.uv.x), min(i.uv.y, 1.0 - i.uv.y));
                float edgeMask = 1.0 - smoothstep(0.0, max(_EdgeWidth, 1e-4), edge);
                col = lerp(col, _EdgeColor.rgb, edgeMask * _EdgeColor.a);

                col += baseCol * _EmissionStrength;
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
}
