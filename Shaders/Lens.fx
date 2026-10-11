// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a piece of glass plus a margin around it (`big` = that whole area's size in
// glass heights; the glass sits in its middle). The glass is a slab with a rounded bevel along its rim (`band`
// wide): the flat middle shows the background undistorted, and so does the very edge, so the picture meets the
// background beside the glass without a jump. In between, the bevel bends the light outwards: what lies inside
// the bevel is pushed in along the outline, so lines near the glass's edge curve with it round its corners.
//  - the bend is u^2 (u = 0 where the bevel starts, 1 at the rim) faded in from zero over the outer 60% of the
//    bevel (smoothstep), so it is smooth at both ends;
//  - it never grows faster than the picture itself (slope < 0.9 even at full strength), so the picture is only
//    ever stretched or squeezed - it can't fold over into mirrored, kinked bits;
//  - red, green and blue bend very slightly differently, giving the faint colour fringe of real glass.
// (The rim's light is drawn by GlassSurface's layers, which follow the glass's exact outline.)
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Lens.ps Lens.fx

sampler2D input : register(s0);
float strength : register(c0); // 0 = flat glass, 1 = strong bending
float aspect   : register(c1); // width / height of the glass
float band     : register(c3); // width of the bevel, in glass heights
float radius   : register(c4); // corner radius of the bevel's rounded rectangle, in glass heights (>= band)
float2 big     : register(c5); // size of the input (glass + margin all round), in glass heights

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * big;                                    // from the glass's centre, in glass heights

    // distance inside the bevel's rounded rectangle and its outward normal
    float2 q = abs(p) - (float2(aspect, 1) * 0.5 - radius);
    float2 qp = max(q, 0);
    float outside = length(qp);
    float d = outside + min(max(q.x, q.y), 0) - radius;           // < 0 inside the glass
    float2 nCorner = qp / max(outside, 0.0001);
    float2 nSide = lerp(float2(0, 1), float2(1, 0), step(q.y, q.x));
    float2 n = lerp(nSide, nCorner, step(0.0001, outside)) * sign(p);

    // bevel: u = 0 where it starts (flat middle), 1 at the rim
    float u = max(1 + d / band, 0);
    // faded in from zero towards the rim, so the very edge shows exactly what's beside the glass
    float ramp = saturate(-d / band / 0.6);
    ramp = ramp * ramp * (3 - 2 * ramp);
    float2 off = n * (strength * band * 0.78 * u * u * ramp) / big;

    // dispersion: red bends a little less, blue a little more (a faint fringe, not a colour split)
    float4 c;
    c.r = tex2D(input, uv + off * 0.97).r;
    c.g = tex2D(input, uv + off).g;
    c.b = tex2D(input, uv + off * 1.03).b;
    c.a = 1;
    return c;
}
