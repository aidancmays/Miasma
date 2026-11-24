# Miasma

Volumetric fog for URP that fills the space. The view is cut into a small 3D grid of froxels (x and y follow the
screen, depth slices grow with distance). Once per froxel, a compute shader works out how much fog is there (height fog
broken into drifting patches, plus any Fog Volumes) and the light it scatters towards the camera: the main light (sun
or moon) in and out of its shadows, which gives shafts; other lights such as torches, within their range; and the
ambient light. Each frame is blended with the last, so the fog is smooth. Each pixel then looks up the fog at its
distance, and beyond the grid the height fog carries on to the horizon, so distant things fade out progressively.

It runs before transparent objects and post-processing, so a grade or a print (Chromolithograph) after it sees the fog.

## Requirements

- Unity 6.3 (6000.3) or newer, Universal Render Pipeline 17.3, Render Graph (the default)
- The renderer's Depth Texture on (the feature asks for it), and compute shader support

## Set up

1. Renderer (for example `PC_Renderer`) > **Add Renderer Feature > Miasma Feature**.
2. On a Volume (global for the default fog, local for an area): **Add Override > Miasma > Fog** and raise **Density**.
3. Optional: **Add Component > Miasma > Fog Volume** on an object for a box of extra fog (or a clearing, with a
   negative density). Its box is the object's transform: a unit cube scaled, turned and moved with it.

## Settings (Miasma Fog override)

| Setting | What it does |
|---|---|
| Density | Fog per metre at and below Base Height. 0.01 haze, 0.05 thick night fog, 0.15 hard to see through. 0 = off. |
| Base Height, Height Falloff | Where the fog sits and how fast it thins above that (higher falloff = ground fog). |
| Noise, Noise Size, Wind | How patchy the fog is, how big the patches are, and how they drift. |
| Albedo | Colour of the fog itself. |
| Forward Scattering | Glow towards lights you look towards (halos, shafts). |
| Main Light, Other Lights, Ambient | How much each kind of light shows in the fog. |
| Distance | Depth of the froxel grid. Shadows, patches, fog volumes and other lights show up to here; beyond it the height fog carries on evenly. |

On the feature: **Method** (Marched: each pixel's ray, with **Steps** and **Half Resolution**; or Froxels: a 3D grid,
with **Quality** and **Smoothing**), **Layer** (Painted watercolour over the finished frame, or In Scene),
**Other Lights**, **Scene View** and **Share Visibility**.

## From code

Settings are ordinary Volume parameters, so a game can set them per level with a runtime profile:

```csharp
var profile = ScriptableObject.CreateInstance<VolumeProfile>();
var fog = profile.Add<Miasma.MiasmaFog>();
fog.density.Override(0.12f);
var volume = gameObject.AddComponent<Volume>();
volume.isGlobal = true;
volume.priority = 100;
volume.sharedProfile = profile;
```

## With a print or outline effect

With **Share Visibility** on, the fog shares how much of each pixel shows through it as the global texture
`_ChromoVisibility` (R: 1 = clear) and sets `_ChromoVisibilityOn` to 1 before transparents. Chromolithograph 0.3 reads
it and fades its key lines, hatching and grain where the fog hides the scene, so fogged things print as flat washes.
Any other effect can read it the same way.

## Notes

- Up to 16 Fog Volumes are drawn at once (the nearest to the camera).
- The grid is coarse, so fog edges and shafts are soft; fast-moving lights leave a short trail (Smoothing).
- Other lights don't cast shadows into the fog (a torch behind a wall still glows through it a little).
- Cost grows with Quality and with the number of other lights (up to 32).
