sampler2D Input : register(s0);
float2 SurfaceSize : register(c0);
float Refraction : register(c1);
float Dispersion : register(c2);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = max(SurfaceSize, float2(1, 1));
    float radius = size.y * 0.5;
    float2 p = (uv - 0.5) * size;
    float halfLine = max(0, (size.x - size.y) * 0.5);
    float2 fromAxis = p - float2(clamp(p.x, -halfLine, halfLine), 0);
    float distance = length(fromAxis);
    float2 normal = fromAxis / max(distance, 0.001);
    float depth = max(0, radius - distance);

    // A stationary curved lens at the capsule boundary, never a moving wave.
    // Leave the central frosted surface untouched; blend out over seven DIP.
    float band = 1 - smoothstep(0.5, 7.0, depth);
    float curvature = band * band;
    float2 bent = clamp(uv - normal * Refraction * curvature / size, 0.002, 0.998);
    float2 split = normal * Dispersion * curvature / size;
    float4 center = tex2D(Input, bent);
    float3 color = center.rgb;
    color.r = tex2D(Input, clamp(bent + split, 0.002, 0.998)).r;
    color.b = tex2D(Input, clamp(bent - split, 0.002, 0.998)).b;

    // Dark-blue glass preserves white lettering on a light desktop.
    color = lerp(color, float3(0.055, 0.09, 0.16), 0.45);
    float keyLight = saturate(dot(normal, float2(-0.55, -0.83)));
    float fineRim = 1 - smoothstep(0, 1.4, depth);
    color += fineRim * (0.04 + 0.12 * keyLight);
    float alpha = center.a * band * 0.65;
    return float4(saturate(color) * alpha, alpha);
}
