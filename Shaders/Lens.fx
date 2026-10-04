// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a piece of glass. Two lens shapes:
//   capsule (slab = 0): long bars. Magnifies across the bar towards its centre line, never along it (the dock
//                       changes width every frame while magnifying; a lens centred on its middle would swim).
//   slab    (slab = 1): panels (previews, Quick Settings). A flat sheet of glass with rounded corners: the
//                       background is bent only in a band along the rim, like light through a bevelled edge,
//                       and the middle is left undistorted (Blur.fx blurs it). The band follows a rounded
//                       rectangle whose corner radius equals the band width, so the bending direction turns
//                       smoothly round the corners and there are no creases.
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Lens.ps Lens.fx

sampler2D input : register(s0);
float strength : register(c0); // 0 = flat glass, 1 = strong lens
float aspect   : register(c1); // width / height of the glass
float edge     : register(c3); // capsule: 1 / ramp share; slab: wider band for smaller values
float slab     : register(c4); // 0 = capsule, 1 = slab

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // capsule: distance to the nearest edge in glass heights, 0 at the rim, 1 on the centre line
    float dx = min(uv.x, 1 - uv.x) * aspect;
    float dy = min(uv.y, 1 - uv.y);
    float ec = saturate(min(dx, dy) * edge);
    float capsule = 1 - (1 - ec) * (1 - ec);
    float2 capsuleUv = float2(uv.x, 0.5 + (uv.y - 0.5) * (1 - strength * 0.97 * capsule));

    // slab: rounded-rectangle distance from the rim, in glass heights
    float2 scale = float2(aspect, 1);
    float2 p = (uv - 0.5) * scale;
    float w = min(aspect, 1) * 0.4 / edge;          // band width (= corner radius of the distance field)
    float2 q = abs(p) - (scale * 0.5 - w);
    float2 qp = max(q, 0);
    float outside = length(qp);
    float d = outside + min(max(q.x, q.y), 0) - w;  // < 0 inside the glass
    float t = saturate(-d / w);                     // 0 at the rim, 1 where the flat middle starts
    // outward normal: radial round the corners, straight along the sides
    float2 nCorner = qp / max(outside, 0.0001);
    float2 nSide = lerp(float2(0, 1), float2(1, 0), step(q.y, q.x));
    float2 n = lerp(nSide, nCorner, step(0.0001, outside)) * sign(p);
    float bend = (1 - t) * (1 - t);
    float2 slabUv = uv - n * (strength * w * 0.8 * bend) / scale;

    float4 c = tex2D(input, lerp(capsuleUv, slabUv, slab));
    c.a = 1;
    return c;
}
