using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;
using UnityEngine.UI;

namespace UIAgent.Tools.Samples
{
    public sealed class AudioMixerSliderIntegrationExample : MonoBehaviour
    {
        [SerializeField] private AudioMixer mixer;
        [SerializeField] private Slider masterVolume;
        [SerializeField] private Slider musicVolume;
        [SerializeField] private Slider effectsVolume;
        [SerializeField] private string masterParameter = "MasterVolume";
        [SerializeField] private string musicParameter = "MusicVolume";
        [SerializeField] private string effectsParameter = "EffectsVolume";
        [SerializeField] private float minimumDecibels = -80f;

        private readonly HashSet<string> warnedParameters = new HashSet<string>();
        private UnityAction<float> masterListener;
        private UnityAction<float> musicListener;
        private UnityAction<float> effectsListener;
        private bool hasStarted;

        private void OnEnable()
        {
            Bind(masterVolume, masterParameter, ref masterListener);
            Bind(musicVolume, musicParameter, ref musicListener);
            Bind(effectsVolume, effectsParameter, ref effectsListener);
            if (hasStarted)
                ApplyCurrentValues();
        }

        private void Start()
        {
            hasStarted = true;
            ApplyCurrentValues();
        }

        private void OnDisable()
        {
            Unbind(masterVolume, masterListener);
            Unbind(musicVolume, musicListener);
            Unbind(effectsVolume, effectsListener);
        }

        private void Bind(Slider slider, string parameter, ref UnityAction<float> listener)
        {
            if (slider == null)
            {
                WarnOnce(parameter, "Assign all three generated Slider references in the Inspector.");
                return;
            }

            listener = value => ApplyVolume(parameter, slider, value);
            slider.onValueChanged.AddListener(listener);
        }

        private void ApplyCurrentValues()
        {
            ApplyIfAssigned(masterVolume, masterParameter);
            ApplyIfAssigned(musicVolume, musicParameter);
            ApplyIfAssigned(effectsVolume, effectsParameter);
        }

        private void ApplyIfAssigned(Slider slider, string parameter)
        {
            if (slider != null)
                ApplyVolume(parameter, slider, slider.value);
        }

        private static void Unbind(Slider slider, UnityAction<float> listener)
        {
            if (slider != null && listener != null)
                slider.onValueChanged.RemoveListener(listener);
        }

        private void ApplyVolume(string parameter, Slider slider, float value)
        {
            if (mixer == null)
            {
                WarnOnce("mixer", "Assign an AudioMixer with exposed volume parameters in the Inspector.");
                return;
            }

            if (string.IsNullOrWhiteSpace(parameter))
            {
                WarnOnce(parameter, "Set each exposed AudioMixer parameter name in the Inspector.");
                return;
            }

            float normalized = Mathf.InverseLerp(slider.minValue, slider.maxValue, value);
            float decibels = Mathf.Lerp(Mathf.Min(minimumDecibels, -0.01f), 0f, normalized);
            if (!mixer.SetFloat(parameter, decibels))
                WarnOnce(parameter, "AudioMixer parameter '" + parameter + "' is not exposed or could not be set.");
        }

        private void WarnOnce(string key, string message)
        {
            key = string.IsNullOrWhiteSpace(key) ? "configuration" : key;
            if (warnedParameters.Add(key))
                Debug.LogWarning(message, this);
        }
    }
}
