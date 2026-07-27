# Rendering

Apogee renders through Vulkan and DirectX 12. The two backends live under
`Source/Engine/GraphicsDevice/` behind one abstraction — `GPUDevice`, `GPUContext`, `GPUBuffer`,
`GPUTexture`, `GPUPipelineState` — and everything above that layer is backend-agnostic.

The backend is chosen at startup from what the platform and the driver offer, and can be forced
from the command line:

```bash
ApogeeGame -vulkan
ApogeeGame -d3d12
ApogeeGame -null        # no rendering; useful for headless runs
```

`-nvidia`, `-amd` and `-intel` hint at which adapter to prefer on a multi-GPU machine.

## The frame

`Renderer::Render(SceneRenderTask*)` is the entry point, and what it drives is an **ordered list of
passes**, not a render graph. `Renderer::Init` registers every pass once —
`GBufferPass`, `ShadowsPass`, `LightPass`, `ForwardPass`, `ReflectionsPass`,
`ScreenSpaceReflectionsPass`, `AmbientOcclusionPass`, `DepthOfFieldPass`, `ColorGradingPass`,
`VolumetricFogPass`, `EyeAdaptationPass`, `PostProcessingPass`, `MotionBlurPass`, the
anti-aliasing passes, `GlobalSignDistanceFieldPass`, `GlobalSurfaceAtlasPass`,
`DynamicDiffuseGlobalIlluminationPass` — and each is a singleton with `Init` / `IsReady` /
`Dispose`. Nothing is scheduled dynamically; the order is written out in `Renderer.cpp` and each
pass decides for itself whether it has work this frame.

Broadly, a frame goes:

1. **Setup.** `RenderSetup` decides up front what the frame needs — motion vectors, TAA jitter,
   Global SDF, the surface atlas, volumetric fog — because several of those change how earlier
   stages must behave, and cannot be discovered halfway through.
2. **Collect.** `Renderer::DrawActors` walks the scene and fills a `RenderList` with draw calls,
   which are then sorted per list type.
3. **Depth and G-buffer.** Deferred material shading writes into the G-buffer.
4. **Shadows and lighting.** Shadow maps, then `LightPass` accumulates into the light buffer.
5. **Reflections, SSR, ambient occlusion, GI.**
6. **Forward.** Transparents and forward-shaded materials render on top.
7. **Post-processing.** Volumetric fog, depth of field, motion blur, eye adaptation, colour
   grading LUT and the post-processing pass, then anti-aliasing (FXAA, SMAA or TAA) and optional
   contrast-adaptive sharpening or upscaling.

Custom work is injected at named points rather than by editing that order. `PostProcessEffect` and
post-fx materials both declare a `PostProcessEffectLocation` / `MaterialPostFxLocation` —
`BeforeReflectionsPass`, `BeforeForwardPass`, `AfterForwardPass`, `BeforePostProcessingPass`,
`AfterPostProcessingPass`, `CustomUpscale` — and the renderer runs them when it reaches that point.

## Render tasks and views

A `SceneRenderTask` is "render this view into these buffers". The main window has one
(`MainRenderTask`); so does every editor viewport, every asset preview, every render-to-texture
control and every `RmlCanvas`. Each carries a `RenderView` (camera, projection, flags, render
mode) and a `RenderBuffers` set, and each is rendered independently.

`ViewFlags` on the view is what turns individual features off for a particular view — shadows,
reflections, fog, GI — which is how an asset preview renders cheaply without changing global
settings. `View.Mode` selects a debug visualization (G-buffer channels, material complexity, quad
overdraw, Global SDF); those go through the same passes rather than a separate path.

## Shaders and materials

Two different things share the word "shader" here:

**Shader assets** are HLSL. Sources live in `Source/Shaders/*.shader` (with `.hlsl` includes), and
`Apogee.Build` generates `Content/Shaders/*.ap` from them. That generated asset is *not* compiled
bytecode — it is a single-chunk asset container wrapping the (obfuscated) source text, with an id
derived deterministically from the shader's name. Bytecode is produced later, per platform and per
permutation: by the editor into the project cache, or by the cooker into the packaged game.

That is why `Content/Shaders/` is generated rather than committed. The asset is a pure function of
the `.shader` beside it, and when it was checked in nothing enforced that the embedded copy still
matched — so it drifted.

```bash
./Build.sh                          # regenerates as part of any build
ApogeeEditor -reimportshaders       # regenerate and exit
ApogeeEditor -shaderdebug           # debug data, optimizations off
ApogeeEditor -shaderprofile         # debug data, optimizations on
```

`Source/Engine/ShadersCompilation/` holds the compiler front-end and its per-backend halves
(DXC/D3D for DirectX, SPIR-V for Vulkan).

**Materials** are authored in the editor's Visject graph and compiled into one of a fixed set of
material shader domains, each with its own generated wrapper:

| Domain | Used for |
| --- | --- |
| `DeferredMaterialShader` | Opaque surfaces that write the G-buffer. The default. |
| `ForwardMaterialShader` | Transparents and anything shaded in the forward pass. |
| `PostFxMaterialShader` | Full-screen effects placed at a post-fx location. |
| `DecalMaterialShader` | Decals projected onto the G-buffer. |
| `GUIMaterialShader` | Materials used as a UI brush. |
| `TerrainMaterialShader`, `DeformableMaterialShader`, `ParticleMaterialShader`, `VolumeParticleMaterialShader` | Their respective renderers. |

A `MaterialInstance` overrides a parent material's parameters without recompiling anything, which
is what you want for per-object variation. `MaterialParams` is the runtime parameter block behind
both.

## Post-process settings

`PostProcessSettings` is a big value type covering ambient occlusion, bloom, tone mapping, colour
grading, eye adaptation, depth of field, motion blur, screen-space reflections and anti-aliasing.
Every group has an override flag, and settings are blended from `PostFxVolume` actors by weight and
priority, so a level can vary grading by region without any code.

Anti-aliasing is selected in those settings: `FXAA`, `SMAA`, `TAA` or none. TAA needs motion
vectors and jitter, which is decided in `RenderSetup` before the frame starts.

## Global illumination

Two systems, both optional and both driven from the same pass list:

- **Global SDF** (`GlobalSignDistanceFieldPass`) builds a signed distance field of the scene, used
  for tracing by GI and by other effects.
- **Global Surface Atlas** (`GlobalSurfaceAtlasPass`) caches lit surface colour for the same
  purpose, and **DDGI** (`DynamicDiffuseGlobalIlluminationPass`) uses both to produce dynamic
  diffuse GI from probe volumes.

Static lighting is baked separately by the lightmapper (`ShadowsOfMordor`) into lightmaps.

## Debugging a frame

- `Apogee.Physics.SetDebugDrawEnabled(true)` for collision shapes; `DebugDraw` for arbitrary
  primitives from code.
- `View.Mode` debug visualizations, from the editor viewport's view menu.
- The profiler windows (`ProfilerGPU`, `ProfilerCPU`) break a frame down by pass and by draw call.
- `-shaderdebug` puts symbol data in compiled shaders for a GPU capture tool.

For the API itself, see [`Renderer`, `GPUDevice` and `RenderTask`](../../api-cpp/index.md) in the
C++ reference.
