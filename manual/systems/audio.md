# Audio

Apogee uses [FMOD Studio](https://www.fmod.com/) for audio, with optional
[Steam Audio](https://valvesoftware.github.io/steam-audio/) spatialization on top of it.

This means the engine does not have a sound-playing API in the usual sense. Sound design happens in
FMOD Studio; the engine plays *events* the designer authored and sets *parameters* the designer
exposed. Anything expressible as "which variation, how loud, how filtered" belongs on the FMOD side
rather than in gameplay code.

FMOD is not redistributed with the engine — see
[Building the engine](../getting-started/building.md#third-party-sdks).

## The pipeline

```text
FMOD Studio project (.fspro)
      │  build
      ▼
  .bank files  ───────►  Content/fmod/
      │  Tools ▸ FMOD ▸ Generate Assets
      ▼
  AudioEvent / AudioBank / AudioBus / AudioVca / AudioSnapshot assets
      │  referenced by
      ▼
  AudioSource actors in a scene
```

The generated assets exist so that an event is picked from a dropdown rather than typed as a
string, and so that the cooker can see which events a scene actually references.

`AudioSettings` configures both ends:

| Setting | What it is |
| --- | --- |
| `FmodStudioProjectFolder` | Where the `.fspro` lives, relative to the project. |
| `GeneratedAssetsFolder` | Where generated content assets are written. Keep it under `Content`. |
| `BankOutputFolder` | Where built `.bank` files live at runtime, relative to `Content`. Defaults to `fmod`. |
| `Banks` | Banks to load at startup. |
| `PreloadSampleData` | Load each bank's sample data immediately — more memory, no first-play streaming hitch. |
| `MasterVolume`, `MasterPitch`, `Muted` | Master bus. |

## Banks

Leave `Banks` empty and every bank found in `BankOutputFolder` is loaded, which is usually what you
want. List them explicitly to control *which* get loaded or in what order. Relative paths resolve
against the project's Content folder; absolute paths are used as-is.

The Master bank and its `.strings` bank must be loaded before any event resolves. This is the first
thing to check when an `AudioSource` silently plays nothing.

Banks always ship as **loose files**, even when the rest of the project's content files are packed
— they stream sample data off disk, and packing them would force the whole bank into memory. That
also means the discovery above works identically in a cooked game and in the editor.

Runtime bank management:

```lua
Apogee.Audio.LoadBank('fmod/Dialogue_EN.bank')
Apogee.Audio.IsBankLoaded('fmod/Dialogue_EN.bank')
Apogee.Audio.UnloadBank('fmod/Dialogue_EN.bank')
```

## Positional sound: AudioSource

`AudioSource` is an **actor**. Place it in the scene, point it at an `AudioEvent`, and it plays a
spatialized instance that follows the actor — driving FMOD's 3D panning, Doppler from the actor's
derived velocity, and optionally Steam Audio occlusion and reflections.

| Field | Effect |
| --- | --- |
| `Event` | The FMOD Studio event to play, picked from the generated assets. |
| `PlayOnStart` | Start automatically when play begins. |
| `StartTime` | Timeline position, in seconds, at which playback starts. |
| `AllowFadeout` | Let the authored release tail play when stopped. |
| `OverrideDistance`, `MinDistance`, `MaxDistance` | Override the event's authored attenuation distances. Do not *also* set an override inside FMOD Studio. |
| `Volume`, `Pitch` | Multipliers applied to this instance. |

```csharp
source.Play();
source.SetParameter("Surface", 2.0f);   // a parameter the sound designer exposed
if (source.IsPlaying) { /* ... */ }
source.Stop();
```

`GetEventLength`, `GetEventPosition` and `SetEventPosition` handle the timeline; `Is3D` reports
whether the event was authored as spatialized at all.

For fire-and-forget sounds with no actor to hang off — an impact, a UI blip — there is:

```lua
Apogee.Audio.PlayOneShot('event:/Weapons/Gunshot', position)
```

## The listener

`AudioListener` is also an actor, and it makes its actor the listener: each play-mode frame it
feeds world position, orientation and derived velocity to the audio system. Put one on the camera
or the player's head. Only one active listener is expected.

If you are driving the listener yourself rather than from a scene actor, `AAudio::SetListener` /
`Apogee.Audio.SetListener(position, forward, up, velocity)` takes it directly.

## Mixing

Buses and VCAs are addressed by their FMOD path:

```lua
Apogee.Audio.SetBusVolume('bus:/SFX', 0.6)
Apogee.Audio.SetBusMuted('bus:/Music', true)
Apogee.Audio.SetBusPaused('bus:/', paused)      -- the master bus
Apogee.Audio.SetVCAVolume('vca:/Dialogue', 0.8)
```

Global (non-event) parameters:

```lua
Apogee.Audio.SetGlobalParameter('Underwater', 1.0)
local v = Apogee.Audio.GetGlobalParameter('Underwater')
```

`SetPaused` on the master bus is the gameplay-pause switch — it stops the mix without tearing down
any instance, so everything resumes exactly where it was.

## Steam Audio

Two per-source switches, both off by default because both cost:

- **`Occlusion`** — occlusion and transmission simulation. `OcclusionRadius` sets the volumetric
  sampling radius. Requires the event to use a Steam Audio Spatializer effect.
- **`Reflections`** — source-specific reflected and reverberated sound. Heavier than occlusion, and
  requires a Steam Audio Reflections effect on the event.

Both also need scene geometry registered with the simulator:

```csharp
var handle = AAudio.AddStaticMesh(vertices, indices);
// ...
AAudio.RemoveStaticMesh(handle);
```

`AAudio.IsSpatialAudioReady` / `Apogee.Audio.IsSpatialAudioReady()` reports whether the Steam Audio
pipeline initialized at all. When it did not, the sources still play — they just fall back to
FMOD's own spatialization.

## Silence, and how to debug it

In rough order of likelihood:

1. No banks loaded, or the Master `.strings` bank is missing. `Apogee.Audio.IsBankLoaded` and the
   startup log will say.
2. No active `AudioListener` in the scene.
3. The event is 3D and the source is out of its attenuation range — check `Is3D` and the
   min/max distances.
4. The master bus is muted or paused. `Apogee.Audio.IsMuted()`, `Apogee.Audio.IsPaused()`.
5. `-mute` on the command line, which forces the null audio backend.

`Apogee.Audio.IsReady()` tells you whether the FMOD system came up at all.

Full API: [`AAudio`, `AudioSource`, `AudioListener`, `AudioSettings`](../../api-cpp/index.md) in
C++, [`Apogee.Audio`](../../api-lua/index.md) in Lua.
