// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a bar. The bar is treated as a thick glass capsule: the background is
// magnified most along the centre line and left undistorted at the rim.
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Lens.ps Lens.fx

sampler2D input : register(s0);
float strength : register(c0); // 0 = flat glass, 1 = strong lens
float aspect   : register(c1); // width / height of the bar
float edge     : register(c3); // 1 / (half-height fraction over which the effect ramps up from the rim)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // Distance to the nearest edge in bar heights: 0 at the rim, rising over the "edge" band.
    float dx = min(uv.x, 1 - uv.x) * aspect;
    float dy = min(uv.y, 1 - uv.y);
    float e = saturate(min(dx, dy) * edge);
    float lens = 1 - (1 - e) * (1 - e);

    // Magnify across the bar towards its centre line. Nothing along the bar: the dock changes width every frame
    // while magnifying, and a lens centred on the bar's middle would make the background swim sideways.
    float2 suv = float2(uv.x, 0.5 + (uv.y - 0.5) * (1 - strength * 0.97 * lens));
    float4 c = tex2D(input, suv);
    c.a = 1;
    return c;
}
