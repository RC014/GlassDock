// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a piece of glass plus a margin around it (`big` = that whole area's size in
// glass heights; the glass sits in its middle). The glass is a slab with a rounded bevel along its rim (`band`
// wide): the flat middle shows the background undistorted, while the bevel bends the light outwards, more and
// more towards the rim, so the rim shows what lies just outside the glass, squeezed along its edge. Lines and
// shapes near the glass therefore curve along its outline and run round its rounded corners with it.
//  - the bend grows as u^2 (u = 0 where the bevel starts, 1 at the rim), up to half the bevel width at full
//    strength, but fades in from zero over the outermost third of the bevel, so right at the edge the glass shows
//    exactly what's beside it and the picture meets the background without a jump (that strip mirrors what's
//    just outside, like the rounded edge of thick glass);
//  - red, green and blue bend very slightly differently, giving the faint colour fringe of real glass at the rim.
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

    // bevel: u = 0 where it starts (flat middle), 1 at the rim (and a little more in corners sharper than the
    // bevel's rounding, so the bend keeps growing smoothly right up to the real edge)
    float u = max(1 + d / band, 0);
    // ...except right at the rim: over the outermost third of the bevel the bend fades in from zero, so at the edge
    // the glass shows exactly what's beside it and the background runs on without a jump
    float ramp = saturate(-d / band * 3);
    ramp = ramp * ramp * (3 - 2 * ramp);
    float2 off = n * (strength * band * 0.5 * u * u * ramp) / big;

    // dispersion: red bends a little less, blue a little more (a faint fringe, not a colour split)
    float4 c;
    c.r = tex2D(input, uv + off * 0.97).r;
    c.g = tex2D(input, uv + off).g;
    c.b = tex2D(input, uv + off * 1.03).b;
    c.a = 1;
    return c;
}
