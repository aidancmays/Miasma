# Miasma

Volumetric fog for URP that fills the space. Every frame it marches along each pixel's view ray through height fog
broken into drifting patches, plus any local fog volumes, and gathers the light the fog scatters towards the camera:
the main light (sun or moon) in and out of its shadows, which gives shafts; other lights such as torches, with their
shadows; and the ambient light. What lies behind fades by how much fog is in front of it.

It runs before transparent objects and post-processing, so a grade or a print (Chromolithograph) after it sees the fog.

## Requirements

- Unity 6.3 (6000.3) or newer, Universal Render Pipeline 17.3, Render Graph (the default)
- The renderer's Depth Texture on (the feature asks for it)

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
| Distance | How far the fog is marched. |

On the feature: **Steps** (quality against speed), **Half Resolution**, **Other Lights** (off saves the most), and
**Scene View**.

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

## Notes

- Up to 16 Fog Volumes are drawn at once (the nearest to the camera).
- The fog is marched at half resolution and laid over the frame with a soft upsample, so very thin objects in front of
  thick fog can show a faint fringe.
- Cost grows with Steps, with the number of lights touching the fog, and with their shadows.
