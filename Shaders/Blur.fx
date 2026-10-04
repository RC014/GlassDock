// One direction of a separable Gaussian blur for GlassDock's glass (WPF ShaderEffect, ps_2_0).
// Applied twice (horizontal, then vertical) after Lens.fx. The radius follows the same rim-to-centre profile as
// the refraction (capsule or dome, see Lens.fx): none at the rim, full in the middle.
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Blur.ps Blur.fx

sampler2D input : register(s0);
float2 dir     : register(c0); // full blur radius in uv units along this pass's direction
float  aspect  : register(c1); // width / height of the glass
float  edge    : register(c2); // same as Lens.fx
float  dome    : register(c3); // 0 = capsule, 1 = dome

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float dx = min(uv.x, 1 - uv.x) * aspect;
    float dy = min(uv.y, 1 - uv.y);
    float ec = saturate(min(dx, dy) * edge);
    float capsule = 1 - (1 - ec) * (1 - ec);

    float2 p = (uv - 0.5) * 2;
    float2 p2 = p * p;
    float s = sqrt(sqrt(p2.x * p2.x + p2.y * p2.y));
    float ed = saturate((1 - s) * edge * 0.5);
    float domeShape = ed * (2 - ed);

    float lens = lerp(capsule, domeShape, dome);

    // 13 taps, Gaussian weights (sigma = 2.5 taps), spread over the radius scaled by lens^2.
    float2 st = dir * (lens * lens / 6);
    float4 c = tex2D(input, uv) * 0.161;
    c += (tex2D(input, uv + st)     + tex2D(input, uv - st))     * 0.149;
    c += (tex2D(input, uv + st * 2) + tex2D(input, uv - st * 2)) * 0.117;
    c += (tex2D(input, uv + st * 3) + tex2D(input, uv - st * 3)) * 0.078;
    c += (tex2D(input, uv + st * 4) + tex2D(input, uv - st * 4)) * 0.045;
    c += (tex2D(input, uv + st * 5) + tex2D(input, uv - st * 5)) * 0.022;
    c += (tex2D(input, uv + st * 6) + tex2D(input, uv - st * 6)) * 0.009;
    c.a = 1;
    return c;
}
