// Copies the finished frame to the surface WPF composes, so no pixels pass through the CPU.
Texture2D<float4> frame : register(t0);
SamplerState linearClamp : register(s0);

struct Corner
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

Corner ShowVertex(uint id : SV_VertexID)
{
    Corner corner;
    corner.uv = float2((id << 1) & 2, id & 2);
    corner.position = float4(corner.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return corner;
}

float4 ShowPixel(Corner corner) : SV_Target
{
    // The compute passes store premultiplied BGRA bytes in an RGBA texture.
    return frame.SampleLevel(linearClamp, corner.uv, 0).bgra;
}
