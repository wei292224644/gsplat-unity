// Copyright (c) 2026 Yize Wu
// SPDX-License-Identifier: MIT
//
// Resolves the gsplat offscreen target into the camera colour target.
//
// The offscreen target holds the whole over-chain evaluated in gamma space and premultiplied:
//   rgb = sum(c_i * a_i * prod(1-a_j))     a = 1 - prod(1-a_i)
// Converting to linear is only correct once that sum is complete, so this pass undoes the
// premultiply to recover the composited gamma-space colour, converts it once, and premultiplies
// again for the One / OneMinusSrcAlpha blend into the camera target.

Shader "Gsplat/Composite"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            Name "GsplatComposite"

            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Same polynomial as UnityCG's GammaToLinearSpace, inlined so this pass is the exact
            // counterpart of the conversion the splat shaders use. Mixing it with the piecewise
            // sRGB curve from the SRP core library would shift colours on its own.
            half3 GsplatGammaToLinear(half3 c)
            {
                return c * (c * (c * 0.305306011h + 0.682171111h) + 0.012522878h);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 acc = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord, 0);

                // Only untouched pixels are dropped. There is no floor below this: rgb is
                // premultiplied, so rgb <= a and the ratio stays bounded however small a gets —
                // the quantisation that would justify a threshold does not exist in float16, and
                // the camera target is not necessarily 8-bit either.
                if (acc.a <= 0.0h)
                    discard;

                half3 composited = acc.rgb / acc.a;
                return half4(GsplatGammaToLinear(composited) * acc.a, acc.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
