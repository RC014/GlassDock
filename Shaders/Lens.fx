// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a piece of glass. The glass is modelled as a slab whose rim is a rounded
// bevel (a quarter-circle profile, `band` wide): looking straight through, the flat middle shows the background
// undistorted, while over the bevel the light is bent towards the inside - gently at first, then very strongly
// at the rim, where the surface turns vertical. That pulls the background just inside the glass out along the
// edge, so lines and shapes passing under the rim visibly wrap around it.
//  - the amount follows the bevel's sag, 1 - sqrt(1 - u^2) (u = 0 where the bevel starts, 1 at the rim);
//  - red, green and blue bend slightly differently, giving the faint rainbow fringe of real glass at the rim.
// (The rim's light is drawn by GlassSurface's layers, which follow the glass's exact outline.)
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Lens.ps Lens.fx

sampler2D input : register(s0);
float strength : register(c0); // 0 = flat glass, 1 = strong bending
float aspect   : register(c1); // width / height of the glass
float band     : register(c3); // width of the bevel, in glass heights
float radius   : register(c4); // corner radius of the bevel's rounded rectangle, in glass heights (>= band)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 scale = float2(aspect, 1);
    float2 p = (uv - 0.5) * scale;

    // distance inside the bevel's rounded rectangle and its outward normal
    float2 q = abs(p) - (scale * 0.5 - radius);
    float2 qp = max(q, 0);
    float outside = length(qp);
    float d = outside + min(max(q.x, q.y), 0) - radius;           // < 0 inside the glass
    float2 nCorner = qp / max(outside, 0.0001);
    float2 nSide = lerp(float2(0, 1), float2(1, 0), step(q.y, q.x));
    float2 n = lerp(nSide, nCorner, step(0.0001, outside)) * sign(p);

    // bevel: u = 0 where it starts (flat middle), 1 at the rim; bend follows the sag of a circular profile
    float u = saturate(1 + d / band);
    float sag = 1 - sqrt(saturate(1 - u * u));
    float2 off = n * (strength * band * 2.0 * sag) / scale;

    // dispersion: red bends a little less, blue a little more
    float4 c;
    c.r = tex2D(input, uv - off * 0.9).r;
    c.g = tex2D(input, uv - off).g;
    c.b = tex2D(input, uv - off * 1.1).b;
    c.a = 1;
    return c;
}
