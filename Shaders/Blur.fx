// One direction of a separable Gaussian blur for GlassDock's glass (WPF ShaderEffect, ps_2_0).
// Applied twice (horizontal, then vertical) after Lens.fx. The radius follows the same rim-to-centre profile as
// the refraction: none at the rim, full along the centre line.
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Blur.ps Blur.fx

sampler2D input : register(s0);
float2 dir     : register(c0); // full blur radius in uv units along this pass's direction
float  aspect  : register(c1); // width / height of the bar
float  edge    : register(c2); // same as Lens.fx

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float dx = min(uv.x, 1 - uv.x) * aspect;
    float dy = min(uv.y, 1 - uv.y);
    float e = saturate(min(dx, dy) * edge);
    float lens = 1 - (1 - e) * (1 - e);

    // 13 taps, Gaussian weights (sigma = 2.5 taps), spread over the radius scaled by lens^2.
    float2 s = dir * (lens * lens / 6);
    float4 c = tex2D(input, uv) * 0.161;
    c += (tex2D(input, uv + s)     + tex2D(input, uv - s))     * 0.149;
    c += (tex2D(input, uv + s * 2) + tex2D(input, uv - s * 2)) * 0.117;
    c += (tex2D(input, uv + s * 3) + tex2D(input, uv - s * 3)) * 0.078;
    c += (tex2D(input, uv + s * 4) + tex2D(input, uv - s * 4)) * 0.045;
    c += (tex2D(input, uv + s * 5) + tex2D(input, uv - s * 5)) * 0.022;
    c += (tex2D(input, uv + s * 6) + tex2D(input, uv - s * 6)) * 0.009;
    c.a = 1;
    return c;
}
