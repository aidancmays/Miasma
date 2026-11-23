# Changelog

## 0.1.0

- Volumetric fog renderer feature: ray-marched height fog with drifting noise, lit by the main light and its shadows,
  additional lights (Forward+ and Forward) and the ambient light, at half resolution.
- `Miasma Fog` volume override for every setting, so fog changes by area and can be set from code per level.
- `Fog Volume` component: local boxes of extra (or less) fog with soft edges.
