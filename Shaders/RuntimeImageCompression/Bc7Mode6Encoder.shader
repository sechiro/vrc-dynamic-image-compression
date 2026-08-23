Shader "Hidden/DigitalRegion/RuntimeImageCompression/BC7Mode6Encoder"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _SourceSize ("Source Size", Vector) = (4, 4, 0.25, 0.25)
        _AlphaErrorWeight ("Alpha Error Weight", Float) = 1
        _FlipSourceY ("Flip Source Y", Float) = 0
        _StripRange ("Block Row Strip [start, end)", Vector) = (0, 1073741824, 0, 0)
        _OutputSrgb ("Store RGB As sRGB", Float) = 0
        _SourceDownscale ("Source Downscale Divisor (1, 2, 4)", Float) = 1
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        HLSLINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _SourceSize;
        float _AlphaErrorWeight;
        float _FlipSourceY;
        float4 _StripRange;
        float _OutputSrgb;
        float _SourceDownscale;

        // Exact piecewise sRGB transfer. UnityCG's LinearToGammaSpace is an
        // approximation that drifts in exactly the dark range this exists for.
        float3 LinearToSrgbExact(float3 value)
        {
            float3 low = value * 12.92;
            float3 high = 1.055 * pow(max(value, 1e-7), 1.0 / 2.4) - 0.055;
            return lerp(high, low, step(value, 0.0031308));
        }

        // uv addresses the center of one encoded texel. _SourceSize describes
        // the encoded (possibly downscaled) size; with _SourceDownscale = d the
        // texel is the box average of the d x d full-resolution texels it
        // covers, computed on linear values before the sRGB transfer.
        uint4 SampleSourceTexel(float2 uv)
        {
            uint divisor = max(1u, (uint)_SourceDownscale);
            float4 sampled;
            if (divisor <= 1u)
            {
                sampled = saturate(tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)));
            }
            else
            {
                float2 fullTexelSize = _SourceSize.zw / (float)divisor;
                float2 origin = uv - 0.5 * _SourceSize.zw + 0.5 * fullTexelSize;
                float4 sum = 0.0;
                [loop]
                for (uint y = 0u; y < divisor; y++)
                {
                    [loop]
                    for (uint x = 0u; x < divisor; x++)
                    {
                        float2 sampleUv = origin + float2(x, y) * fullTexelSize;
                        sum += saturate(tex2Dlod(_MainTex, float4(sampleUv, 0.0, 0.0)));
                    }
                }
                sampled = sum / (float)(divisor * divisor);
            }

            if (_OutputSrgb > 0.5)
            {
                sampled.rgb = LinearToSrgbExact(sampled.rgb);
            }
            return uint4(round(sampled * 255.0));
        }

        static const uint kBc7Weights[16] =
        {
             0u,  4u,  9u, 13u, 17u, 21u, 26u, 30u,
            34u, 38u, 43u, 47u, 51u, 55u, 60u, 64u
        };

        static const uint kBc7ProjectionSteps[64] =
        {
             0u,  0u,  0u,  1u,  1u,  1u,  1u,  2u,
             2u,  2u,  2u,  2u,  3u,  3u,  3u,  3u,
             4u,  4u,  4u,  4u,  5u,  5u,  5u,  5u,
             6u,  6u,  6u,  6u,  6u,  7u,  7u,  7u,
             7u,  8u,  8u,  8u,  8u,  9u,  9u,  9u,
             9u, 10u, 10u, 10u, 10u, 10u, 11u, 11u,
            11u, 11u, 12u, 12u, 12u, 12u, 13u, 13u,
            13u, 13u, 14u, 14u, 14u, 14u, 15u, 15u
        };

        uint4 DecodePaletteValue(uint4 endpoint0, uint4 endpoint1, uint index)
        {
            uint weight = kBc7Weights[index];
            return ((64u - weight) * endpoint0 + weight * endpoint1 + 32u) >> 6u;
        }

        float WeightedLengthSquared(float4 value)
        {
            float alphaWeight = max(0.0, _AlphaErrorWeight);
            return dot(value.rgb, value.rgb) + alphaWeight * value.a * value.a;
        }

        float GetPaletteError(uint4 pixel, uint4 endpoint0, uint4 endpoint1, uint index)
        {
            int4 difference = int4(DecodePaletteValue(endpoint0, endpoint1, index)) - int4(pixel);
            return WeightedLengthSquared(float4(difference));
        }

        uint FindProjectionIndex(uint4 pixel, uint4 endpoint0, uint4 endpoint1)
        {
            float4 start = float4(endpoint0);
            float4 span = float4(endpoint1) - start;
            float4 delta = float4(pixel) - start;
            float alphaWeight = max(0.0, _AlphaErrorWeight);
            float spanLengthSquared = dot(span.rgb, span.rgb) + alphaWeight * span.a * span.a;

            if (spanLengthSquared <= 0.0)
            {
                return 0u;
            }

            float projection = dot(span.rgb, delta.rgb) + alphaWeight * span.a * delta.a;
            if (projection <= 0.0)
            {
                return 0u;
            }
            if (projection >= spanLengthSquared)
            {
                return 15u;
            }

            uint step = min(63u, (uint)(projection * 63.49999 / spanLengthSquared));
            return kBc7ProjectionSteps[step];
        }

        uint4 PackMode6Block(uint4 endpoint0, uint4 endpoint1, uint indices[16])
        {
            uint4 block = 0u;

            block.x = 0x40u
                | ((endpoint0.r & 0xFEu) <<  6u)
                | ((endpoint1.r & 0xFEu) << 13u)
                | ((endpoint0.g & 0xFEu) << 20u)
                | ((endpoint1.g & 0xFEu) << 27u);

            block.y = ((endpoint1.g & 0xFEu) >>  5u)
                | ((endpoint0.b & 0xFEu) <<  2u)
                | ((endpoint1.b & 0xFEu) <<  9u)
                | ((endpoint0.a & 0xFEu) << 16u)
                | ((endpoint1.a & 0xFEu) << 23u)
                | ((endpoint0.r & 0x01u) << 31u);

            block.z = (endpoint1.r & 0x01u)
                | ((indices[0] & 0x07u) <<  1u)
                | ((indices[1] & 0x0Fu) <<  4u)
                | ((indices[2] & 0x0Fu) <<  8u)
                | ((indices[3] & 0x0Fu) << 12u)
                | ((indices[4] & 0x0Fu) << 16u)
                | ((indices[5] & 0x0Fu) << 20u)
                | ((indices[6] & 0x0Fu) << 24u)
                | ((indices[7] & 0x0Fu) << 28u);

            block.w = ((indices[ 8] & 0x0Fu) <<  0u)
                | ((indices[ 9] & 0x0Fu) <<  4u)
                | ((indices[10] & 0x0Fu) <<  8u)
                | ((indices[11] & 0x0Fu) << 12u)
                | ((indices[12] & 0x0Fu) << 16u)
                | ((indices[13] & 0x0Fu) << 20u)
                | ((indices[14] & 0x0Fu) << 24u)
                | ((indices[15] & 0x0Fu) << 28u);

            return block;
        }

        void EncodeCandidate(
            uint4 pixels[16],
            uint4 endpointSeed0,
            uint4 endpointSeed1,
            uint p0,
            uint p1,
            out uint4 packedBlock,
            out float totalError)
        {
            uint4 endpoint0 = (endpointSeed0 & 0xFEu) | p0;
            uint4 endpoint1 = (endpointSeed1 & 0xFEu) | p1;
            uint indices[16];
            totalError = 0.0;

            [unroll]
            for (uint i = 0u; i < 16u; i++)
            {
                uint index = FindProjectionIndex(pixels[i], endpoint0, endpoint1);
                float pixelError = GetPaletteError(pixels[i], endpoint0, endpoint1, index);

                // The projection table is continuous-space guidance. Rechecking its
                // neighbors against the integer BC7 palette avoids rounding misses.
                if (index > 0u)
                {
                    float lowerError = GetPaletteError(pixels[i], endpoint0, endpoint1, index - 1u);
                    if (lowerError < pixelError)
                    {
                        index--;
                        pixelError = lowerError;
                    }
                }
                if (index < 15u)
                {
                    float upperError = GetPaletteError(pixels[i], endpoint0, endpoint1, index + 1u);
                    if (upperError < pixelError)
                    {
                        index++;
                        pixelError = upperError;
                    }
                }

                indices[i] = index;
                totalError += pixelError;
            }

            // Mode 6 stores only three bits for texel zero. Swapping endpoints and
            // inverting every index preserves the palette while satisfying the anchor.
            if (indices[0] >= 8u)
            {
                uint4 temporaryEndpoint = endpoint0;
                endpoint0 = endpoint1;
                endpoint1 = temporaryEndpoint;

                [unroll]
                for (uint j = 0u; j < 16u; j++)
                {
                    indices[j] = 15u - indices[j];
                }
            }

            packedBlock = PackMode6Block(endpoint0, endpoint1, indices);
        }

        void EvaluateEndpointSeeds(
            uint4 pixels[16],
            uint4 endpointSeed0,
            uint4 endpointSeed1,
            out uint4 bestBlock,
            out float bestError)
        {
            uint4 candidateBlock;
            float candidateError;

            EncodeCandidate(pixels, endpointSeed0, endpointSeed1, 0u, 0u, bestBlock, bestError);

            EncodeCandidate(pixels, endpointSeed0, endpointSeed1, 1u, 0u, candidateBlock, candidateError);
            if (candidateError < bestError)
            {
                bestBlock = candidateBlock;
                bestError = candidateError;
            }

            EncodeCandidate(pixels, endpointSeed0, endpointSeed1, 0u, 1u, candidateBlock, candidateError);
            if (candidateError < bestError)
            {
                bestBlock = candidateBlock;
                bestError = candidateError;
            }

            EncodeCandidate(pixels, endpointSeed0, endpointSeed1, 1u, 1u, candidateBlock, candidateError);
            if (candidateError < bestError)
            {
                bestBlock = candidateBlock;
                bestError = candidateError;
            }
        }

        // Loads the 16 texels of one block in row-major order (texel i is at
        // local (i & 3, i >> 2)). Coordinates past the source edge clamp to
        // the last texel so non-block-aligned sources get edge padding.
        void LoadBlockPixels(
            uint2 blockCoordinate,
            out uint4 pixels[16],
            out uint4 endpointMinimum,
            out uint4 endpointMaximum)
        {
            endpointMinimum = 255u;
            endpointMaximum = 0u;

            [unroll]
            for (uint i = 0u; i < 16u; i++)
            {
                uint2 localCoordinate = uint2(i & 3u, i >> 2u);
                uint2 sourceCoordinate = blockCoordinate * 4u + localCoordinate;
                uint2 maximumSourceCoordinate = uint2(
                    (uint)_SourceSize.x - 1u,
                    (uint)_SourceSize.y - 1u);
                sourceCoordinate = min(sourceCoordinate, maximumSourceCoordinate);
                if (_FlipSourceY > 0.5)
                {
                    sourceCoordinate.y = (uint)_SourceSize.y - 1u - sourceCoordinate.y;
                }

                float2 uv = (float2(sourceCoordinate) + 0.5) * _SourceSize.zw;
                uint4 pixel = SampleSourceTexel(uv);
                pixels[i] = pixel;
                endpointMinimum = min(endpointMinimum, pixel);
                endpointMaximum = max(endpointMaximum, pixel);
            }
        }

        uint4 EncodeMode6Block(uint2 blockCoordinate)
        {
            uint4 pixels[16];
            uint4 endpointMinimum;
            uint4 endpointMaximum;
            LoadBlockPixels(blockCoordinate, pixels, endpointMinimum, endpointMaximum);

            uint4 bestBlock;
            uint4 candidateBlock;
            float bestError;
            float candidateError;

            EvaluateEndpointSeeds(pixels, endpointMinimum, endpointMaximum, bestBlock, bestError);

            // Component-wise bounds can describe colors that do not occur in the
            // block. Also test the most widely separated pair of actual pixels.
            uint4 farthestEndpoint0 = pixels[0];
            uint4 farthestEndpoint1 = pixels[0];
            float farthestDistance = -1.0;
            [unroll]
            for (uint first = 0u; first < 16u; first++)
            {
                [unroll]
                for (uint second = 0u; second < 16u; second++)
                {
                    if (second > first)
                    {
                        float distance = WeightedLengthSquared(
                            float4(int4(pixels[second]) - int4(pixels[first])));
                        if (distance > farthestDistance)
                        {
                            farthestDistance = distance;
                            farthestEndpoint0 = pixels[first];
                            farthestEndpoint1 = pixels[second];
                        }
                    }
                }
            }

            EvaluateEndpointSeeds(
                pixels,
                farthestEndpoint0,
                farthestEndpoint1,
                candidateBlock,
                candidateError);
            if (candidateError < bestError)
            {
                bestBlock = candidateBlock;
            }

            return bestBlock;
        }

        uint GetPackedWord(v2f_img input)
        {
            uint outputX = (uint)floor(input.pos.x);
            uint outputY = (uint)floor(input.pos.y);

            // The controller encodes a bounded strip of block rows per frame
            // into the same render target. Rows outside the strip must exit
            // before the encode so their cost is not paid in this frame.
            if (outputY < (uint)_StripRange.x || outputY >= (uint)_StripRange.y)
            {
                discard;
                return 0u;
            }

            uint lane = outputX & 3u;
            uint2 blockCoordinate = uint2(outputX >> 2u, outputY);
            uint4 packedBlock = EncodeMode6Block(blockCoordinate);

            if (lane == 0u) return packedBlock.x;
            if (lane == 1u) return packedBlock.y;
            if (lane == 2u) return packedBlock.z;
            return packedBlock.w;
        }

        // ---------------------------------------------------------------
        // BC1 (DXT1): 8-byte blocks for opaque sources.
        //   byte 0-1  color0 (RGB565, little-endian)
        //   byte 2-3  color1 (RGB565, little-endian)
        //   byte 4-7  16 x 2-bit index, texel 0 in the low bits of byte 4
        // Blocks are always written in four-color mode (color0 > color1).
        // A flat block has color0 == color1 and every index zero. Alpha is
        // ignored; the controller only routes alpha-less sources here.
        // ---------------------------------------------------------------

        uint QuantizeRgb565(uint3 rgb)
        {
            uint r5 = (rgb.r * 31u + 127u) / 255u;
            uint g6 = (rgb.g * 63u + 127u) / 255u;
            uint b5 = (rgb.b * 31u + 127u) / 255u;
            return (r5 << 11u) | (g6 << 5u) | b5;
        }

        // Decoders expand by replicating the high bits, so errors are
        // measured against the expanded values rather than the 5/6-bit ones.
        uint3 ExpandRgb565(uint color)
        {
            uint r5 = (color >> 11u) & 31u;
            uint g6 = (color >> 5u) & 63u;
            uint b5 = color & 31u;
            return uint3(
                (r5 << 3u) | (r5 >> 2u),
                (g6 << 2u) | (g6 >> 4u),
                (b5 << 3u) | (b5 >> 2u));
        }

        void EncodeBc1Candidate(
            uint4 pixels[16],
            uint3 endpointSeed0,
            uint3 endpointSeed1,
            out uint2 packedBlock,
            out float totalError)
        {
            uint color0 = QuantizeRgb565(endpointSeed0);
            uint color1 = QuantizeRgb565(endpointSeed1);

            // Four-color mode requires color0 > color1. Ordering the
            // endpoints before index selection keeps the palette fixed.
            if (color0 < color1)
            {
                uint temporaryColor = color0;
                color0 = color1;
                color1 = temporaryColor;
            }

            uint3 palette[4];
            palette[0] = ExpandRgb565(color0);
            palette[1] = ExpandRgb565(color1);
            palette[2] = (2u * palette[0] + palette[1] + 1u) / 3u;
            palette[3] = (palette[0] + 2u * palette[1] + 1u) / 3u;

            uint indexBits = 0u;
            totalError = 0.0;

            [unroll]
            for (uint i = 0u; i < 16u; i++)
            {
                uint bestIndex = 0u;
                float bestError = 3.4e38;

                [unroll]
                for (uint j = 0u; j < 4u; j++)
                {
                    int3 difference = int3(palette[j]) - int3(pixels[i].rgb);
                    float candidateError = dot(float3(difference), float3(difference));
                    if (candidateError < bestError)
                    {
                        bestError = candidateError;
                        bestIndex = j;
                    }
                }

                indexBits |= bestIndex << (2u * i);
                totalError += bestError;
            }

            if (color0 == color1)
            {
                indexBits = 0u;
            }

            packedBlock = uint2(color0 | (color1 << 16u), indexBits);
        }

        uint2 EncodeBc1Block(uint2 blockCoordinate)
        {
            uint4 pixels[16];
            uint4 endpointMinimum;
            uint4 endpointMaximum;
            LoadBlockPixels(blockCoordinate, pixels, endpointMinimum, endpointMaximum);

            uint2 bestBlock;
            uint2 candidateBlock;
            float bestError;
            float candidateError;

            EncodeBc1Candidate(
                pixels,
                endpointMinimum.rgb,
                endpointMaximum.rgb,
                bestBlock,
                bestError);

            // Pulling the bounds inward by 1/16 of the range usually lowers
            // the error of smooth blocks because more texels land on the
            // two interpolated palette entries.
            uint3 inset = (endpointMaximum.rgb - endpointMinimum.rgb) >> 4u;
            EncodeBc1Candidate(
                pixels,
                endpointMinimum.rgb + inset,
                endpointMaximum.rgb - inset,
                candidateBlock,
                candidateError);
            if (candidateError < bestError)
            {
                bestBlock = candidateBlock;
                bestError = candidateError;
            }

            // Component-wise bounds can describe colors that do not occur in
            // the block. Also test the most widely separated pair of texels.
            uint3 farthestEndpoint0 = pixels[0].rgb;
            uint3 farthestEndpoint1 = pixels[0].rgb;
            float farthestDistance = -1.0;
            [unroll]
            for (uint first = 0u; first < 16u; first++)
            {
                [unroll]
                for (uint second = 0u; second < 16u; second++)
                {
                    if (second > first)
                    {
                        int3 difference = int3(pixels[second].rgb) - int3(pixels[first].rgb);
                        float distance = dot(float3(difference), float3(difference));
                        if (distance > farthestDistance)
                        {
                            farthestDistance = distance;
                            farthestEndpoint0 = pixels[first].rgb;
                            farthestEndpoint1 = pixels[second].rgb;
                        }
                    }
                }
            }

            EncodeBc1Candidate(
                pixels,
                farthestEndpoint0,
                farthestEndpoint1,
                candidateBlock,
                candidateError);
            if (candidateError < bestError)
            {
                bestBlock = candidateBlock;
            }

            return bestBlock;
        }

        // One BC1 block is two 32-bit words, so the render target is
        // blockWidth * 2 pixels wide and the lane is the low bit of x.
        uint GetPackedWordBc1(v2f_img input)
        {
            uint outputX = (uint)floor(input.pos.x);
            uint outputY = (uint)floor(input.pos.y);

            if (outputY < (uint)_StripRange.x || outputY >= (uint)_StripRange.y)
            {
                discard;
                return 0u;
            }

            uint lane = outputX & 1u;
            uint2 blockCoordinate = uint2(outputX >> 1u, outputY);
            uint2 packedBlock = EncodeBc1Block(blockCoordinate);

            return lane == 0u ? packedBlock.x : packedBlock.y;
        }
        ENDHLSL

        // Pass 0: preferred path. One signed 32-bit render-target pixel per word.
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex vert_img
            #pragma fragment FragRInt

            int FragRInt(v2f_img input) : SV_Target
            {
                return asint(GetPackedWord(input));
            }
            ENDHLSL
        }

        // Pass 1: portable fallback. The same word is split into four UNorm8 channels.
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex vert_img
            #pragma fragment FragArgb32

            float4 FragArgb32(v2f_img input) : SV_Target
            {
                uint word = GetPackedWord(input);
                return float4(
                    word & 0xFFu,
                    (word >> 8u) & 0xFFu,
                    (word >> 16u) & 0xFFu,
                    (word >> 24u) & 0xFFu) / 255.0;
            }
            ENDHLSL
        }

        // Pass 2: BC1 (DXT1), one signed 32-bit render-target pixel per word.
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex vert_img
            #pragma fragment FragRIntBc1

            int FragRIntBc1(v2f_img input) : SV_Target
            {
                return asint(GetPackedWordBc1(input));
            }
            ENDHLSL
        }

        // Pass 3: BC1 (DXT1), portable fallback split into four UNorm8 channels.
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex vert_img
            #pragma fragment FragArgb32Bc1

            float4 FragArgb32Bc1(v2f_img input) : SV_Target
            {
                uint word = GetPackedWordBc1(input);
                return float4(
                    word & 0xFFu,
                    (word >> 8u) & 0xFFu,
                    (word >> 16u) & 0xFFu,
                    (word >> 24u) & 0xFFu) / 255.0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
