Shader "NeonCompanion/SplashBackground"
{
    // ---------------------------------------------------------------
    // Full-screen background for the splash / loading screen.
    // Renders two soft Gaussian radial glows (teal + purple) over a
    // dark base colour.  Attach to a world-space Quad that fills the
    // camera frustum; the UIDocument panel is transparent on top.
    // ---------------------------------------------------------------
    Properties
    {
        _BaseColor     ("Base Color",           Color)  = (0.020, 0.031, 0.063, 1)
        _TealColor     ("Teal Glow Color",      Color)  = (0.000, 0.765, 0.686, 1)
        _PurpleColor   ("Purple Glow Color",    Color)  = (0.608, 0.118, 0.902, 1)
        _TealPos       ("Teal Centre (UV)",     Vector) = (0.18, 0.62, 0, 0)
        _PurplePos     ("Purple Centre (UV)",   Vector) = (0.82, 0.68, 0, 0)
        _TealRadius    ("Teal Falloff Radius",  Float)  = 0.55
        _PurpleRadius  ("Purple Falloff Radius",Float)  = 0.50
        _TealStrength  ("Teal Strength",        Float)  = 0.32
        _PurpleStrength("Purple Strength",      Float)  = 0.26
        _PulseSpeed    ("Pulse Speed",          Float)  = 1.4
        _PulseAmp      ("Pulse Amplitude",      Float)  = 0.12
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Background-1"
        }

        Pass
        {
            Name "SplashBG"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite Off
            ZTest  Always
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _BaseColor;
                half4  _TealColor;
                half4  _PurpleColor;
                float4 _TealPos;
                float4 _PurplePos;
                float  _TealRadius;
                float  _PurpleRadius;
                float  _TealStrength;
                float  _PurpleStrength;
                float  _PulseSpeed;
                float  _PulseAmp;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv         = v.uv;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;

                // Aspect-ratio correction so glows are round, not oval
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 uvA = float2(uv.x * aspect, uv.y);

                float2 tealCentreA   = float2(_TealPos.x   * aspect, _TealPos.y);
                float2 purpleCentreA = float2(_PurplePos.x * aspect, _PurplePos.y);

                // Gaussian falloff (same formula as CSS radial-gradient stop)
                float td = length(uvA - tealCentreA)   / max(_TealRadius,   0.001);
                float pd = length(uvA - purpleCentreA) / max(_PurpleRadius, 0.001);

                float teal   = exp(-td * td * 2.8) * _TealStrength;
                float purple = exp(-pd * pd * 2.8) * _PurpleStrength;

                // Subtle breathing pulse
                float t      = _Time.y * _PulseSpeed;
                float pulse1 = 1.0 + _PulseAmp * sin(t);
                float pulse2 = 1.0 + _PulseAmp * sin(t * 0.73 + 1.57);

                half3 col = _BaseColor.rgb;
                col += _TealColor.rgb   * (teal   * pulse1);
                col += _PurpleColor.rgb * (purple * pulse2);

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
