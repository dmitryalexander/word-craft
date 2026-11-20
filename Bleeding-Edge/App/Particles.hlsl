#include "SumiInk.hlsl"

// Each particle is a small soft square drawn additively, so dense flow reads bright and swept regions stay dark.
struct Spark
{
    float4 position : SV_Position;
    float2 corner : TEXCOORD0;
    float speed : TEXCOORD1;
    float4 glow : TEXCOORD2;
};

Spark ShowParticle(uint id : SV_VertexID)
{
    uint width, height;
    source.GetDimensions(width, height);
    uint index = id / 6, which = id % 6;
    // xy is the position across the window from 0 to 1, zw the motion per step.
    float4 particle = source.Load(int3(index % width, index / width, 0));
    float2 corner = float2(which == 1 || which == 3 || which == 5,
                           which == 2 || which == 4 || which == 5) * 2 - 1;
    float2 place = float2(particle.x * 2 - 1, 1 - particle.y * 2);
    Spark spark;
    spark.position = float4(place + corner * radius * 2 / float2(size), 0, 1);
    spark.corner = corner;
    spark.speed = length(particle.zw * float2(size));
    spark.glow = field.Load(int3(index % width, index / width, 0));
    return spark;
}

float4 GlowParticle(Spark spark) : SV_Target
{
    float core = saturate(1 - dot(spark.corner, spark.corner));
    float heat = saturate(spark.speed * phase * 0.15);
    float3 cool = particleSlow.rgb, bright = particleFast.rgb;
    float3 color = heat < 0.5 ? lerp(cool, bright, heat * 2) : lerp(bright, float3(1, 1, 1), heat * 2 - 1);
    // Particles made by a keystroke shine in the glow color, then fade into the swarm.
    float shine = spark.glow.a;
    color = lerp(color, spark.glow.rgb / max(shine, 0.001), shine) * (1 + shine * ink);
    return float4(color * core * strength, 1);
}
