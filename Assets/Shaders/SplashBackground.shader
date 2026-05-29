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

        // ── Conic blob behind the icon ────────────────────────────────
        _ConicColor1   ("Conic Colour 1 (cyan)",    Color)  = (0.30, 0.78, 0.92, 1)
        _ConicColor2   ("Conic Colour 2 (indigo)",  Color)  = (0.49, 0.48, 0.93, 1)
        _ConicColor3   ("Conic Colour 3 (magenta)", Color)  = (0.88, 0.45, 0.78, 1)
        _ConicCenter   ("Conic Centre (UV)",        Vector) = (0.50, 0.62, 0, 0)
        _ConicRadius   ("Conic Radius (UV-aspect)", Float)  = 0.20
        _ConicStrength ("Conic Strength",           Float)  = 0.55
        _ConicSpeed    ("Conic Rotation Speed",     Float)  = 0.35
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
            // Universal2D — required tag so URP 2D Renderer actually
            // dispatches this pass. UniversalForward would render only on
            // the 3D forward renderer; the project uses Renderer2D.asset.
            Tags { "LightMode" = "Universal2D" }

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
                half4  _ConicColor1;
                half4  _ConicColor2;
                half4  _ConicColor3;
                float4 _ConicCenter;
                float  _ConicRadius;
                float  _ConicStrength;
                float  _ConicSpeed;
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
                // SCREEN-space UV (0..1 across viewport), NOT mesh UV.
                // The BackgroundQuad is 40×40 world units but the camera only
                // sees a 17.78×10 slice — mesh UV maps to the whole quad, so
                // UV.y=0.62 would end up near the top of the visible viewport.
                // positionCS.xy / _ScreenParams gives true screen UV [0,1].
                // URP fragment shader's positionCS is already Y-up (Y=0 at
                // bottom) — no flip needed.
                float2 uv = i.positionCS.xy / _ScreenParams.xy;

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

                // ── Conic blob behind the icon ────────────────────────
                // 3-stop conic loop (c1 → c2 → c3 → c1) with rotation +
                // gaussian radial falloff so it fades to nothing past the
                // icon. Cheap: one atan2, one exp, three lerps.
                float2 conicCentreA = float2(_ConicCenter.x * aspect, _ConicCenter.y);
                float2 toConic      = uvA - conicCentreA;
                float  conicDist    = length(toConic) / max(_ConicRadius, 0.001);
                // Tight gaussian (factor 4.0) keeps the blob localised to the
                // icon area instead of bleeding across the whole viewport.
                float  conicFall    = exp(-conicDist * conicDist * 4.0) * _ConicStrength;

                float angle  = atan2(toConic.y, toConic.x) + _Time.y * _ConicSpeed;
                float angleT = frac(angle / 6.2831853 + 0.5);   // [0,1] from atan2 range

                half3 conicCol;
                if      (angleT < 0.3333) conicCol = lerp(_ConicColor1.rgb, _ConicColor2.rgb,  angleT          * 3.0);
                else if (angleT < 0.6667) conicCol = lerp(_ConicColor2.rgb, _ConicColor3.rgb, (angleT - 0.3333) * 3.0);
                else                      conicCol = lerp(_ConicColor3.rgb, _ConicColor1.rgb, (angleT - 0.6667) * 3.0);

                col += conicCol * conicFall;

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
