#include "SumiInk.hlsl"

struct ProjectedLight
{
    float4 position : SV_Position;
    float2 flat : TEXCOORD0;
    float2 bent : TEXCOORD1;
};

// A regular mesh samples the wave height field. Rasterization takes place at the
// refracted landing positions so overlapping folds add light on the basin floor.
ProjectedLight ProjectLight(uint id : SV_VertexID)
{
    uint2 tile = uint2((id / 6) % size.x, (id / 6) / size.x);
    uint corner = id % 6;
    uint2 offset = uint2(corner == 1 || corner == 3 || corner == 5, corner == 2 || corner == 4 || corner == 5);
    float2 uv = float2(tile + offset) / size;
    float2 cell = 1.0 / float2(384, 216);
    float2 dx = float2(cell.x, 0), dy = float2(0, cell.y);
    float h = WaterSurfaceHeight(uv);
    float2 slope = float2(WaterSurfaceHeight(uv + dx) - WaterSurfaceHeight(uv - dx),
                          WaterSurfaceHeight(uv + dy) - WaterSurfaceHeight(uv - dy));
    float3 normal = normalize(float3(-waterBend * slope - WaterDriftSlope(uv), 1));

    float3 incident = Sunlight();
    float3 ray = refract(incident, normal, 1.0 / 1.333);
    float3 flatRay = refract(incident, float3(0, 0, 1), 1.0 / 1.333);
    float2 aspect = float2(0.5625, 1);
    float2 flatOffset = flatRay.xy / -flatRay.z * waterDepth * aspect;
    float2 bent = uv + ray.xy / max(0.40, -ray.z) * (waterDepth + 0.013 * h) * aspect - flatOffset;
    ProjectedLight output;
    output.position = float4(bent.x * 2 - 1, 1 - bent.y * 2, 0, 1);
    output.flat = uv;
    output.bent = bent;
    return output;
}

float TriangleArea(float2 x, float2 y)
{
    return abs(x.x * y.y - x.y * y.x);
}

float FocusLight(ProjectedLight input) : SV_Target
{
    float before = TriangleArea(ddx(input.flat), ddy(input.flat));
    float after = TriangleArea(ddx(input.bent), ddy(input.bent));
    // Finite highlight width also limits the energy of near-degenerate triangles.
    return min(8, before / max(after, 1e-9));
}
