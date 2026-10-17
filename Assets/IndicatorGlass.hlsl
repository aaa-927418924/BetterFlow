sampler2D Input : register(s0);
float2 SurfaceSize : register(c0);
float Refraction : register(c1);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = max(SurfaceSize, float2(1, 1));
    float radius = size.y * 0.5;
    float2 p = (uv - 0.5) * size;
    float halfLine = max(0, (size.x - size.y) * 0.5);
    float2 offset = p - float2(clamp(p.x, -halfLine, halfLine), 0);
    float distance = length(offset);
    float2 normal = offset / max(distance, 0.001);
    float edge = saturate(1 - (radius - distance) / 6.0);
    float2 bent = uv - normal * (Refraction * edge * edge) / size;
    bent = clamp(bent, 0.002, 0.998);
    float2 dispersion = normal * (0.32 * edge * edge) / size;
    float4 color = tex2D(Input, bent);
    color.r = tex2D(Input, clamp(bent + dispersion, 0.002, 0.998)).r;
    color.b = tex2D(Input, clamp(bent - dispersion, 0.002, 0.998)).b;
    float rim = pow(edge, 5);
    float light = saturate(dot(normal, float2(-0.55, -0.83)));
    color.rgb += (0.035 + 0.08 * light) * rim * color.a;
    color.rgb *= 1 - 0.04 * edge * edge * (1 - light);
    return color;
}
