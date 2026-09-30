Snow Depth RT - 4-Channel 2x2 Layout
====================================

Overview
--------
A single physical render target (2048x2048, R8G8B8A8_UNorm) is used to
represent a 4096x4096 logical depth map. Each physical texel stores 4
logical depth samples in its RGBA channels, arranged in a 2x2
neighborhood pattern.

Memory footprint is identical to a 4096x4096 R8_UNorm texture (16 MB),
but the physical RT stays at 2048x2048. This is useful when RT size is
constrained but a larger logical resolution is desired.


Channel-to-Logical-Pixel Mapping
--------------------------------
Physical texel (lx, ly) maps to a 2x2 block of logical pixels:

    Logical (2lx,     2ly)     ->  .r  (top-left)
    Logical (2lx + 1, 2ly)     ->  .g  (top-right)
    Logical (2lx,     2ly + 1) ->  .b  (bottom-left)
    Logical (2lx + 1, 2ly + 1) ->  .a  (bottom-right)

Visually, the logical image is tiled as:

    r g r g r g ...
    b a b a b a ...
    r g r g r g ...
    b a b a b a ...

Every 2x2 logical block corresponds to one physical texel's RGBA
channels.


Write Path (Footprint Draw CS)
------------------------------
- One thread processes one physical texel.
- The thread reads the existing float4 value, computes a separate
  falloff for each channel using that channel's logical pixel center,
  and writes back a float4.
- Falloff per channel:
    .r: distance from logical (2lx + 0.5, 2ly + 0.5)
    .g: distance from logical (2lx + 1.5, 2ly + 0.5)
    .b: distance from logical (2lx + 0.5, 2ly + 1.5)
    .a: distance from logical (2lx + 1.5, 2ly + 1.5)
- PixelCenter and PixelRadius are in logical 4096 space.
- StartOffset is in physical texel space.
- The outermost ring of physical texels is never written, to avoid
  sampling artifacts.


Read Path (Material Sampling)
-----------------------------
- Custom Code 1 computes the physical texel UV for the current, right,
  and up logical pixels, plus the channel index for the current logical
  pixel.
- Three Sample Texture nodes (point sampling) fetch the float4 at those
  UVs.
- Custom Code 2 uses the channel index to select the correct component
  from each float4, then computes the normal, roughness, specular, and
  SSS.


Bounding Box Dispatch
---------------------
- Logical bounds are computed from PixelCenter +/- PixelRadius in
  logical space.
- Logical bounds are converted to physical texel bounds via integer
  division by 2.
- Dispatch groups are aligned to 8x8 and computed from the physical
  bounds.


Known Trade-offs
----------------
- Hardware bilinear filtering does not interpolate across channels, so
  neighboring logical pixels coming from different channels can show
  visible steps at the logical pixel level.
- To smooth this, the read path would need manual bilinear interpolation
  across the 4 channels, which adds cost.
- For most cases the steps are small and visually acceptable, especially
  at larger radii.


Notes
-----
- This layout is a backup option. The primary layout is single-channel
  4096x4096 R8_UNorm, which uses hardware interpolation and is simpler.
- Keep the 4-channel version for special cases where a 4096x4096 RT is
  not available or not desirable.