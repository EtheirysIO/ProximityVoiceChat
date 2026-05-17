## Spatialization

There's a pretty big rabbit hole to go down to get accurate audio spatialization. At the simplest level, spatialization can be done by panning audio based on how far left or right the audio source is from the listener. The amount to pan has different implementations, but this method overall has limitations. This [StackOverflow post](https://stackoverflow.com/a/67081750) gives a good explanation of the limitations and the idea behind methods for improved accuracy.

One of the methods is to use Head-Related Transfer Functions (HRTF), which are a collection mathematical functions that can transform an input audio stream to simulate being in a specific spatial position (some can be found [here](https://github.com/csound/csound/tree/develop/samples)). These functions are found through real-world measurements and alter all parts of an audio stream, from changing amplitudes to modulating frequencies. The resulting output stereo audio is called binaural audio. The math behind applying these functions is computationally heavy, however, as the input audio must be convoluted with the HRTF impulse response data. Convolution is a time-based operation (see N-point finite impulse response filter), so even if the math can be optimized, audio latency must be introduced to properly execute the convolution.

This is then all to say, trying to create binaural audio for spatialization is not viable for this project, and it's better to just stick with simple panning.

## Cleanup pass — investigations parked for follow-up (v1.1.4)

### `INameplateGui` v15 migration (D4)

`UI/NameplateVoiceOverlay.cs` currently uses
`IAddonLifecycle.RegisterListener(AddonEvent.PostDraw, "NamePlate", …)` plus an
unsafe walk of `UI3DModule.NamePlateObjectInfoPointers` to map nameplates back
to game objects. Dalamud v15 exposes a `Dalamud.Game.Gui.NamePlate` namespace
that contains the building blocks of a higher-level API:

- `INamePlateInfoView` — "read-only view of the nameplate info object data for a nameplate"
- `INamePlateUpdateContext` — "information related to the pending nameplate data update"
- `INamePlateUpdateHandler` — "a class representing a single nameplate"
- `NamePlateKindConversions`, `NamePlateQuotedParts`, `NamePlateSimpleParts`
- `NamePlateKind`, `NamePlateStringField`

The exposing service is presumably `INamePlateGui` (the v15 docs namespace
landing page only lists the namespace contents, not the service signature). A
migration would replace the unsafe pointer walk with the typed handler and
should simplify both the per-frame loop and the lookup from nameplate →
`IGameObject`. **Risk**: icon positioning currently uses screen coordinates
derived from the addon's `RootNode` and a slight pixel offset; the new API may
emit world-space or differently-clipped coordinates. Migrate behind a smoke
test that compares pre/post icon placement on the same player at the same
camera angle.

Action: not migrated in this pass. Open as its own ticket so the diff is small
and reviewable on its own.

### Custom `WaveInEvent.cs` / `WaveInBuffer.cs` diff vs NAudio 2.2.1 (F)

`Audio/WaveInEvent.cs` and `Audio/WaveInBuffer.cs` shadow NAudio's same-named
classes. NAudio is pinned at 2.2.1 in `EtheirysProximityVoiceChat.csproj`. Comparing
the local copies to the upstream source at the `v2.2.1` tag
(`naudio/NAudio` on GitHub) shows mostly cosmetic divergence — but two
behavioral patches and one latent bug.

**`WaveInEvent.cs` — non-cosmetic differences**

1. `DoRecording()` (local lines 163-191): after `callbackEvent.WaitOne()`
   returns, the local copy adds
   ```csharp
   if (captureState != CaptureState.Capturing) { return; }
   ```
   immediately, **before** iterating the buffers. NAudio's version always
   iterates the buffers (and dispatches `DataAvailable` for any with data) on
   wake-up, exiting via the `while (captureState == CaptureState.Capturing)`
   condition only on the next loop. Effect: on `StopRecording()`, the local
   copy discards the final wake-up's pending buffer data; NAudio flushes it.
   This is deliberate (probably to avoid emitting a noisy stale frame after
   the user releases push-to-talk), but it's invisible from the outside.

2. `StopRecording()` (local lines 217-226): adds
   `&& captureState != CaptureState.Stopping` to the guard, so a re-entrant
   call while already stopping is a no-op. NAudio's only checks `!= Stopped`,
   which means a second `StopRecording()` between `Stopping` and `Stopped`
   double-calls `waveInStop`/`waveInReset` and double-sets the callback event.
   Defensive patch; almost certainly worth keeping.

3. `DoRecording()` (local line 151): adds `if (buffers == null) { return; }`
   guard. Cannot fire in practice (StartRecording always opens the device,
   which creates buffers, before queueing this thread) — pure null-flow-safe
   hygiene.

Everything else (nullable annotations, `initialState:` named arg, `IDisposable`
explicit redeclaration on `: IWaveIn, IDisposable`, literal `4u`/`4` for
`MmTime.TIME_BYTES`, missing some `<summary>` XML comments → `// Summary:`
style comments) is cosmetic.

**`WaveInBuffer.cs` — non-cosmetic differences**

1. `Dispose(bool)` (local lines 108-130) wraps the final
   `waveInUnprepareHeader` call in `MmException.Try(...)`, which **throws** on
   non-`MMSYSERR_NOERROR` return codes. NAudio's version calls it bare and
   discards the result. Because `Dispose(false)` is reachable via the
   finalizer (`~WaveInBuffer() → Dispose(false)`), and throwing in a finalizer
   crashes the process, this is a **latent bug**. The fix is one line: drop
   the `MmException.Try(...)` wrap and call `WaveInterop.waveInUnprepareHeader(...)`
   directly, matching NAudio.

2. `nint` vs `IntPtr` everywhere — identical type since C# 9, just sugar.

3. `Dispose(true)` no longer has the empty `if (disposing) { /* free managed
   resources */ }` block. Cosmetic — there's nothing to do in that branch.

**Recommendation**

Two options, both low-risk:

- **Minimum:** fix the `MmException.Try` bug in `WaveInBuffer.Dispose(bool)`,
  keep everything else. The two `WaveInEvent` behavioral patches are worth
  keeping.
- **Maximum:** delete `Audio/WaveInEvent.cs` and `Audio/WaveInBuffer.cs`
  entirely and use `NAudio.Wave.WaveInEvent` directly in
  `Audio/AudioDeviceController.cs`. Saves ~430 LOC but loses the two
  defensive patches above — must be paired with a recording-stop smoke test
  to confirm no stale tail frames or double-stop misbehavior.

Action: not changed in this pass. Open as its own ticket.

