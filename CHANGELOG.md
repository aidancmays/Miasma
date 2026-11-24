# Changelog

## 0.2.0

- Method: Marched (default; each half-resolution pixel's ray is marched, now with an animated step offset and a soft
  upsample, and carrying on past the marched distance) or Froxels (a camera-aligned 3D grid worked out in a compute
  shader, smoothed over frames; cheaper and softer).
- Layer: Painted (default) lays the fog over the finished frame as watercolour (washes, blooms that change shape in
  place, granulation, wet edges, all fixed in space), or In Scene blends it into the scene as before.
- Cimmerian: how much the fog swallows light instead of glowing with it.

- Rewritten as froxel fog: density and light are worked out once per cell of a camera-aligned 3D grid in a compute
  shader (blended with last frame's, reprojected), added up front to back, and looked up per pixel. Much cheaper, and
  the per-pixel dither pattern is gone. Settings: Quality (grid size) and Smoothing replace Steps and Half Resolution.
- Beyond the grid the height fog carries on, worked out exactly, so distant things and the sky keep fading out.
- Other lights only light fog within their range, with an even glow, at half strength by default (they no longer
  tint the whole view).
- Shares how much of each pixel shows through the fog (`_ChromoVisibility`), so a print effect such as
  Chromolithograph 0.3 can fade its lines and texture in the fog (Share Visibility on the feature).

## 0.1.0

- Volumetric fog renderer feature: ray-marched height fog with drifting noise, lit by the main light and its shadows,
  additional lights (Forward+ and Forward) and the ambient light, at half resolution.
- `Miasma Fog` volume override for every setting, so fog changes by area and can be set from code per level.
- `Fog Volume` component: local boxes of extra (or less) fog with soft edges.
