// Glass refraction for GlassDock (WPF ShaderEffect, ps_2_0). First pass; Blur.fx softens it afterwards.
// The input is the screen area behind a piece of glass plus a margin around it (`big` = that whole area's size in
// glass heights; the glass sits in its middle).
// The glass works like a lens: thin at its edge, so the background there is barely refracted and the picture meets
// what's beside the glass without a jump; thicker further in, refracting more and more; and on big glass a plateau
// of constant thickness from the end of the rounded edge (`band` in) to the middle, where the refraction stays the
// same. The refraction is the magnification of a lens: the background is enlarged towards the glass's middle by an
// amount that follows the thickness, so it grows smoothly from nothing at the edge to a constant enlargement on the
// plateau (straight lines stay straight there), and lines near the edge curve with it round the corners.
//  - Enlarging towards the middle by less near the edge than further in can't fold the picture, at any strength.
//  - Long bars are enlarged across their height only (the zoom along their length is scaled down to match), so
//    every edge refracts alike.
//  - red, green and blue are enlarged very slightly differently: the faint colour fringe of real glass.
// (The rim's light is drawn by GlassSurface's layers, which follow the glass's exact outline.)
// Compile: fxc /T ps_2_0 /E main /O3 /Fo Lens.ps Lens.fx

sampler2D input : register(s0);
float strength : register(c0); // 0 = flat glass, 1 = strong lens
float aspect   : register(c1); // width / height of the glass
float band     : register(c3); // width of the rounded edge (where the thickness grows), in glass heights
float radius   : register(c4); // corner radius of the edge's rounded rectangle, in glass heights (>= band)
float2 big     : register(c5); // size of the input (glass + margin all round), in glass heights

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * big;                                    // from the glass's centre, in glass heights
    float2 halfSize = float2(aspect, 1) * 0.5;

    // depth inside the glass's rounded rectangle -> thickness: 0 at the edge, rising along a circular arc to 1 at `band` in
    float2 q = abs(p) - (halfSize - radius);
    float d = length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;  // < 0 inside the glass
    float t = saturate(-d / band);
    float thick = sqrt(t * (2 - t));                                // a lens's edge: a circular arc, steep at the rim, level at the plateau

    // magnification on the plateau: the shift there is about `strength` x 0.6 x the edge width at the plateau's
    // border; across the short side, and scaled down along a long side so both shift alike
    float shortHalf = min(halfSize.x, halfSize.y);
    float zoom = min(strength * 0.6 * band / shortHalf, 0.7);
    float2 z = zoom * shortHalf / halfSize;
    float2 off = p * z * thick / big;                              // towards the middle

    // dispersion: red enlarged a little less, blue a little more (a faint fringe, not a colour split)
    float4 c;
    c.r = tex2D(input, uv - off * 0.97).r;
    c.g = tex2D(input, uv - off).g;
    c.b = tex2D(input, uv - off * 1.03).b;
    c.a = 1;
    return c;
}
