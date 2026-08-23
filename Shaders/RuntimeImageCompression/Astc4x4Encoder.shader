Shader "Hidden/DigitalRegion/RuntimeImageCompression/ASTC4x4Encoder"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _SourceSize ("Source Size", Vector) = (4, 4, 0.25, 0.25)
        _AlphaErrorWeight ("Alpha Error Weight", Float) = 1
        _FlipSourceY ("Flip Source Y", Float) = 0
        _FlipOutputY ("Flip Output Block Rows", Float) = 0
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
        float _FlipOutputY;
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

        static const uint kAstcWeights[4] = { 0u, 21u, 43u, 64u };

        uint4 DecodePaletteValue16(uint4 endpoint0, uint4 endpoint1, uint index)
        {
            uint weight = kAstcWeights[index];
            uint4 endpoint16_0 = endpoint0 * 257u;
            uint4 endpoint16_1 = endpoint1 * 257u;
            return ((64u - weight) * endpoint16_0 + weight * endpoint16_1 + 32u) >> 6u;
        }

        float GetPaletteError(uint4 pixel, uint4 endpoint0, uint4 endpoint1, uint index)
        {
            int4 difference = int4(DecodePaletteValue16(endpoint0, endpoint1, index))
                - int4(pixel * 257u);
            float4 value = float4(difference);
            return dot(value.rgb, value.rgb) + max(0.0, _AlphaErrorWeight) * value.a * value.a;
        }

        float WeightedLengthSquared(float4 value)
        {
            return dot(value.rgb, value.rgb)
                + max(0.0, _AlphaErrorWeight) * value.a * value.a;
        }

        // Fixed ASTC configuration used by this proof of concept:
        // 4x4 weights, QUANT_4, one partition, one plane, and CEM 12 RGBA-direct.
        // Endpoints are stored as eight unquantized bytes. Weight ISE bits occupy
        // the high 32 bits of the physical block in reverse-stream order.
        uint4 PackAstcBlock(uint4 endpoint0, uint4 endpoint1, uint indices[16])
        {
            uint endpointValues[8];
            endpointValues[0] = endpoint0.r;
            endpointValues[1] = endpoint1.r;
            endpointValues[2] = endpoint0.g;
            endpointValues[3] = endpoint1.g;
            endpointValues[4] = endpoint0.b;
            endpointValues[5] = endpoint1.b;
            endpointValues[6] = endpoint0.a;
            endpointValues[7] = endpoint1.a;

            uint4 block = 0u;
            block.x = 0x00018042u
                | (endpointValues[0] << 17u)
                | ((endpointValues[1] & 0x7Fu) << 25u);
            block.y = (endpointValues[1] >> 7u)
                | (endpointValues[2] << 1u)
                | (endpointValues[3] << 9u)
                | (endpointValues[4] << 17u)
                | ((endpointValues[5] & 0x7Fu) << 25u);
            block.z = (endpointValues[5] >> 7u)
                | (endpointValues[6] << 1u)
                | (endpointValues[7] << 9u);

            [unroll]
            for (uint i = 0u; i < 16u; i++)
            {
                uint index = indices[i] & 3u;
                uint reversedIndexBits = ((index & 1u) << 1u) | ((index >> 1u) & 1u);
                block.w |= reversedIndexBits << (30u - 2u * i);
            }

            return block;
        }

        void EvaluateEndpointSeeds(
            uint4 pixels[16],
            uint4 endpoint0,
            uint4 endpoint1,
            out uint4 packedBlock,
            out float totalError)
        {
            // CEM 12 otherwise invokes Blue Contraction when endpoint 0 has
            // the greater RGB sum. Reordering before index selection keeps the
            // direct RGBA endpoint interpretation.
            if (endpoint0.r + endpoint0.g + endpoint0.b
                > endpoint1.r + endpoint1.g + endpoint1.b)
            {
                uint4 temporaryEndpoint = endpoint0;
                endpoint0 = endpoint1;
                endpoint1 = temporaryEndpoint;
            }

            uint indices[16];
            totalError = 0.0;
            [unroll]
            for (uint pixelIndex = 0u; pixelIndex < 16u; pixelIndex++)
            {
                uint bestIndex = 0u;
                float bestError = GetPaletteError(pixels[pixelIndex], endpoint0, endpoint1, 0u);

                [unroll]
                for (uint candidateIndex = 1u; candidateIndex < 4u; candidateIndex++)
                {
                    float candidateError = GetPaletteError(
                        pixels[pixelIndex],
                        endpoint0,
                        endpoint1,
                        candidateIndex);
                    if (candidateError < bestError)
                    {
                        bestIndex = candidateIndex;
                        bestError = candidateError;
                    }
                }

                indices[pixelIndex] = bestIndex;
                totalError += bestError;
            }

            packedBlock = PackAstcBlock(endpoint0, endpoint1, indices);
        }

        uint4 EncodeAstcBlock(uint2 blockCoordinate)
        {
            uint4 pixels[16];
            uint4 endpointMinimum = 255u;
            uint4 endpointMaximum = 0u;

            [unroll]
            for (uint i = 0u; i < 16u; i++)
            {
                uint2 localCoordinate = uint2(i & 3u, i >> 2u);
                uint2 sourceCoordinate = min(
                    blockCoordinate * 4u + localCoordinate,
                    uint2((uint)_SourceSize.x - 1u, (uint)_SourceSize.y - 1u));

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

            uint4 bestBlock;
            uint4 candidateBlock;
            float bestError;
            float candidateError;
            EvaluateEndpointSeeds(
                pixels,
                endpointMinimum,
                endpointMaximum,
                bestBlock,
                bestError);

            // Component bounds may form colors that never occur in the block.
            // A two-sweep farthest-pixel search cheaply finds a second line
            // candidate that preserves common two-color and chroma edges.
            uint farthestFromFirstIndex = 0u;
            float farthestDistance = -1.0;
            [unroll]
            for (uint firstSweep = 0u; firstSweep < 16u; firstSweep++)
            {
                float distance = WeightedLengthSquared(
                    float4(int4(pixels[firstSweep]) - int4(pixels[0])));
                if (distance > farthestDistance)
                {
                    farthestDistance = distance;
                    farthestFromFirstIndex = firstSweep;
                }
            }

            uint farthestFromSecondIndex = farthestFromFirstIndex;
            farthestDistance = -1.0;
            [unroll]
            for (uint secondSweep = 0u; secondSweep < 16u; secondSweep++)
            {
                float distance = WeightedLengthSquared(
                    float4(int4(pixels[secondSweep]) - int4(pixels[farthestFromFirstIndex])));
                if (distance > farthestDistance)
                {
                    farthestDistance = distance;
                    farthestFromSecondIndex = secondSweep;
                }
            }

            EvaluateEndpointSeeds(
                pixels,
                pixels[farthestFromFirstIndex],
                pixels[farthestFromSecondIndex],
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
            // before the encode so their cost is not paid in this frame. The
            // strip is expressed in physical output rows, before any flip.
            if (outputY < (uint)_StripRange.x || outputY >= (uint)_StripRange.y)
            {
                discard;
                return 0u;
            }

            uint lane = outputX & 3u;

            // When the transport probe found the readback rows reversed, write
            // block rows in reverse so the readback arrives in natural order
            // and the controller never has to re-order the payload on the CPU.
            uint blockY = outputY;
            if (_FlipOutputY > 0.5)
            {
                uint lastBlockRow = (((uint)_SourceSize.y + 3u) >> 2u) - 1u;
                blockY = lastBlockRow - min(outputY, lastBlockRow);
            }

            uint2 blockCoordinate = uint2(outputX >> 2u, blockY);
            uint4 packedBlock = EncodeAstcBlock(blockCoordinate);

            if (lane == 0u) return packedBlock.x;
            if (lane == 1u) return packedBlock.y;
            if (lane == 2u) return packedBlock.z;
            return packedBlock.w;
        }

        uint GetTransportProbeByte(uint byteIndex)
        {
            if (byteIndex == 0u) return 0u;
            if (byteIndex == 1u) return 255u;
            if (byteIndex == 2u) return 127u;
            if (byteIndex == 3u) return 128u;
            return (byteIndex * 73u + 19u) & 255u;
        }

        uint GetTransportProbeWord(v2f_img input)
        {
            uint2 pixel = uint2(floor(input.pos.xy));
            uint byteIndex = (pixel.y * 4u + pixel.x) * 4u;
            return GetTransportProbeByte(byteIndex)
                | (GetTransportProbeByte(byteIndex + 1u) << 8u)
                | (GetTransportProbeByte(byteIndex + 2u) << 16u)
                | (GetTransportProbeByte(byteIndex + 3u) << 24u);
        }
        ENDHLSL

        // Preferred path when an integer render target survives the device stack.
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 gles3 metal vulkan
            #pragma vertex vert_img
            #pragma fragment FragRInt

            int FragRInt(v2f_img input) : SV_Target
            {
                return asint(GetPackedWord(input));
            }
            ENDHLSL
        }

        // GLES3-compatible byte transport fallback.
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 gles3 metal vulkan
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


        // RInt transport sentinel. The controller requires all 64 bytes to
        // survive exactly before it trusts this backend for ASTC payloads.
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 gles3 metal vulkan
            #pragma vertex vert_img
            #pragma fragment FragProbeRInt

            int FragProbeRInt(v2f_img input) : SV_Target
            {
                return asint(GetTransportProbeWord(input));
            }
            ENDHLSL
        }

        // ARGB32 transport sentinel. Linear UNorm output is deliberate: the
        // probe rejects channel swizzles or any sRGB conversion.
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 gles3 metal vulkan
            #pragma vertex vert_img
            #pragma fragment FragProbeArgb32

            float4 FragProbeArgb32(v2f_img input) : SV_Target
            {
                uint word = GetTransportProbeWord(input);
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
