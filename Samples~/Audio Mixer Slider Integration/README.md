# Audio Mixer Slider Integration

This optional sample connects three generated uGUI Sliders to exposed Unity `AudioMixer` float parameters. The generated Prefab stays generic: it contains native Slider state and visual references, but no game-specific callbacks or persistence.

1. In an Audio Mixer, expose three volume parameters, for example `MasterVolume`, `MusicVolume`, and `EffectsVolume`. Connect those exposed parameters to the matching group faders.
2. Add a generated settings Prefab to a scene and make sure the scene has one active `EventSystem` with the input module used by the project.
3. Import this sample from **Window > Package Manager > UI Agent Tools > Samples**.
4. Add `AudioMixerSliderIntegrationExample` to an active scene object.
5. Assign the Audio Mixer and the three Slider components in the Inspector. Set each parameter field to the exact exposed parameter name in that mixer.
6. Enter Play Mode and move the sliders. The sample maps each Slider's normalized range to `minimumDecibels..0 dB`; the default minimum is `-80 dB`.

The sample first applies current values in `Start`, reapplies them on subsequent enables, and unregisters its cached runtime listeners when disabled. The mapping is linear in decibels: a normalized value of 0.5 means -40 dB with the default range, not 50% linear amplitude. It does not save preferences, choose the project's audio policy, or alter the generated Prefab. To persist settings, store the normalized Slider values in the consuming game's own settings system. Runtime Inspector rebinding is outside this example's contract.

Initial verification compiled the sample and checked listener lifecycles without a mixer. The later Menu Flow Sandbox demo added a real Master/Music/Effects mixer and self-authored test tones: exposed parameter changes, independent subgroup routing, and AudioListener output attenuation passed automated checks in Unity 2022.3. This verifies the digital signal path; speaker output and subjective listening remain manual. See the source project's `docs/menu-flow-integration.md` and `examples/generation/reports/deep-space-menu-flow-20261023/README.md` for current evidence.
