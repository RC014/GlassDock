// One direction of a separable Gaussian blur for GlassDock's glass (WPF ShaderEffect, ps_2_0).
// Applied twice (horizontal, then vertical) after Lens.fx. The radius follows the lens shape (see Lens.fx):
// capsule: none at the rim, full on the centre line; slab: none at the rim, building across the refracting
// band to full over the flat middle.
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Blur.ps Blur.fx

sampler2D input : register(s0);
float2 dir     : register(c0); // full blur radius in uv units along this pass's direction
float  aspect  : register(c1); // width / height of the glass
float  edge    : register(c2); // same as Lens.fx
float  slab    : register(c3); // 0 = capsule, 1 = slab

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float dx = min(uv.x, 1 - uv.x) * aspect;
    float dy = min(uv.y, 1 - uv.y);
    float ec = saturate(min(dx, dy) * edge);
    float capsule = 1 - (1 - ec) * (1 - ec);

    float2 scale = float2(aspect, 1);
    float2 p = (uv - 0.5) * scale;
    float w = min(aspect, 1) * 0.4 / edge;
    float2 q = abs(p) - (scale * 0.5 - w);
    float d = length(max(q, 0)) + min(max(q.x, q.y), 0) - w;
    float t = saturate(-d / w);
    float slabShape = t * t * (3 - 2 * t);

    float lens = lerp(capsule, slabShape, slab);

    // 11 taps, Gaussian weights (sigma = 2.5 taps; the outermost pair's weight folded into the centre to stay
    // within ps_2_0's instruction budget), spread over the radius scaled by lens^2.
    float2 st = dir * (lens * lens / 5);
    float4 c = tex2D(input, uv) * 0.179;
    c += (tex2D(input, uv + st)     + tex2D(input, uv - st))     * 0.149;
    c += (tex2D(input, uv + st * 2) + tex2D(input, uv - st * 2)) * 0.117;
    c += (tex2D(input, uv + st * 3) + tex2D(input, uv - st * 3)) * 0.078;
    c += (tex2D(input, uv + st * 4) + tex2D(input, uv - st * 4)) * 0.045;
    c += (tex2D(input, uv + st * 5) + tex2D(input, uv - st * 5)) * 0.022;
    c.a = 1;
    return c;
}
