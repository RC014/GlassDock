// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a piece of glass, treated as a flat sheet of glass with rounded corners:
// the background is bent only in a band along the rim, like light through a bevelled edge, and the middle is
// left undistorted (Blur.fx blurs it). The band follows a rounded rectangle with the glass's corner radius (at
// least the band width, so the bending direction turns smoothly round the corners and there are no creases).
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Lens.ps Lens.fx

sampler2D input : register(s0);
float strength : register(c0); // 0 = flat glass, 1 = strong bending
float aspect   : register(c1); // width / height of the glass
float band     : register(c3); // width of the refracting band, in glass heights
float radius   : register(c4); // corner radius of the distance field, in glass heights (>= band)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // rounded-rectangle distance from the rim, in glass heights
    float2 scale = float2(aspect, 1);
    float2 p = (uv - 0.5) * scale;
    float2 q = abs(p) - (scale * 0.5 - radius);
    float2 qp = max(q, 0);
    float outside = length(qp);
    float d = outside + min(max(q.x, q.y), 0) - radius; // < 0 inside the glass
    float t = saturate(-d / band);                       // 0 at the rim, 1 where the flat middle starts
    // outward normal: radial round the corners, straight along the sides
    float2 nCorner = qp / max(outside, 0.0001);
    float2 nSide = lerp(float2(0, 1), float2(1, 0), step(q.y, q.x));
    float2 n = lerp(nSide, nCorner, step(0.0001, outside)) * sign(p);
    float bend = (1 - t) * (1 - t);
    float4 c = tex2D(input, uv - n * (strength * band * 0.8 * bend) / scale);
    c.a = 1;
    return c;
}
