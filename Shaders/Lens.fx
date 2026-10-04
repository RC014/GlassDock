// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a piece of glass. Two lens shapes:
//   capsule (dome = 0): long bars. Magnifies across the bar towards its centre line, never along it (the dock
//                       changes width every frame while magnifying; a lens centred on its middle would swim).
//   dome    (dome = 1): squarer panels (previews, Quick Settings). Magnifies towards the centre in both
//                       directions using a superellipse distance, which is smooth everywhere (a min() of the
//                       distances to the sides would leave straight creases running in from the corners).
// Both are strongest in the middle and fade to undistorted at the rim.
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Lens.ps Lens.fx

sampler2D input : register(s0);
float strength : register(c0); // 0 = flat glass, 1 = strong lens
float aspect   : register(c1); // width / height of the glass
float edge     : register(c3); // 1 / (share of the rim-to-centre distance over which the effect builds up)
float dome     : register(c4); // 0 = capsule, 1 = dome

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // capsule: distance to the nearest edge in glass heights, 0 at the rim, 1 on the centre line
    float dx = min(uv.x, 1 - uv.x) * aspect;
    float dy = min(uv.y, 1 - uv.y);
    float ec = saturate(min(dx, dy) * edge);
    float capsule = 1 - (1 - ec) * (1 - ec);
    float2 capsuleUv = float2(uv.x, 0.5 + (uv.y - 0.5) * (1 - strength * 0.97 * capsule));

    // dome: superellipse radius (n = 4), 0 at the centre and 1 at the rim
    float2 p = (uv - 0.5) * 2;
    float2 p2 = p * p;
    float s = sqrt(sqrt(p2.x * p2.x + p2.y * p2.y));
    float ed = saturate((1 - s) * edge * 0.5);
    float domeShape = ed * (2 - ed);
    float2 domeUv = 0.5 + (uv - 0.5) * (1 - strength * 0.5 * domeShape);

    float4 c = tex2D(input, lerp(capsuleUv, domeUv, dome));
    c.a = 1;
    return c;
}
