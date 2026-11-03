using System;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UIAgent.Tools.Samples
{
    /// <summary>Example policy: preview immediately, persist only on explicit Save.</summary>
    public sealed class VolumeSettingsIntegrationExample : MonoBehaviour
    {
        [SerializeField] private Slider masterVolume;
        [SerializeField] private Slider musicVolume;
        [SerializeField] private Slider effectsVolume;
        [SerializeField] private Button saveButton;
        [SerializeField] private Button defaultsButton;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private string preferenceKey = "UIAgent.Tools.Samples.MenuFlow.Volume.v1";
        [SerializeField] private Vector3 defaultNormalized = new Vector3(0.82f, 0.65f, 0.72f);
        private bool applying;
        public int SaveCount { get; private set; }
        public int PreviewChangeCount { get; private set; }
        public string PreferenceKey => preferenceKey;

        private void OnEnable()
        {
            if (masterVolume == null || musicVolume == null || effectsVolume == null ||
                saveButton == null || defaultsButton == null || statusLabel == null || string.IsNullOrWhiteSpace(preferenceKey))
            {
                Debug.LogError("Volume settings sample: assign sliders, buttons, status and a project-specific preference key.", this);
                enabled = false;
                return;
            }
            saveButton.onClick.AddListener(Save);
            defaultsButton.onClick.AddListener(RestoreDefaults);
            masterVolume.onValueChanged.AddListener(OnPreviewChanged);
            musicVolume.onValueChanged.AddListener(OnPreviewChanged);
            effectsVolume.onValueChanged.AddListener(OnPreviewChanged);
        }

        private void Start() => ReloadSaved();

        private void OnDisable()
        {
            if (saveButton != null) saveButton.onClick.RemoveListener(Save);
            if (defaultsButton != null) defaultsButton.onClick.RemoveListener(RestoreDefaults);
            if (masterVolume != null) masterVolume.onValueChanged.RemoveListener(OnPreviewChanged);
            if (musicVolume != null) musicVolume.onValueChanged.RemoveListener(OnPreviewChanged);
            if (effectsVolume != null) effectsVolume.onValueChanged.RemoveListener(OnPreviewChanged);
        }

        public void Save()
        {
            try
            {
                Vector3 value = CurrentNormalized();
                var json = new JObject { ["version"] = 1, ["master"] = value.x, ["music"] = value.y, ["effects"] = value.z };
                PlayerPrefs.SetString(preferenceKey, json.ToString(Newtonsoft.Json.Formatting.None));
                PlayerPrefs.Save();
                SaveCount++;
                Show("SAVED", value);
            }
            catch (PlayerPrefsException exception)
            {
                statusLabel.text = "SAVE FAILED / " + exception.Message;
                Debug.LogWarning("Volume preferences could not be saved.", this);
            }
        }

        public void ReloadSaved()
        {
            Vector3 value = defaultNormalized;
            bool exists = PlayerPrefs.HasKey(preferenceKey);
            bool valid = exists && TryDecode(PlayerPrefs.GetString(preferenceKey), out value);
            if (!valid) value = defaultNormalized;
            Apply(value);
            Show(valid ? "LOADED" : exists ? "INVALID SAVE / DEFAULT PREVIEW" : "DEFAULT PREVIEW", CurrentNormalized());
        }

        public void RestoreDefaults()
        {
            Apply(defaultNormalized);
            Show("DEFAULT PREVIEW / PRESS SAVE", CurrentNormalized());
        }

        public static bool TryDecode(string json, out Vector3 value)
        {
            value = Vector3.zero;
            try
            {
                var record = JObject.Parse(json);
                if (record.Count != 4 || record["version"]?.Type != JTokenType.Integer || (int)record["version"] != 1) return false;
                string[] names = { "master", "music", "effects" };
                var numbers = new float[3];
                for (int i = 0; i < names.Length; i++)
                {
                    JToken token = record[names[i]];
                    if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
                    float number = (float)token;
                    if (float.IsNaN(number) || float.IsInfinity(number) || number < 0f || number > 1f) return false;
                    numbers[i] = number;
                }
                value = new Vector3(numbers[0], numbers[1], numbers[2]);
                return true;
            }
            catch (Exception exception) when (exception is Newtonsoft.Json.JsonException || exception is ArgumentException || exception is OverflowException)
            {
                return false;
            }
        }

        private void Apply(Vector3 value)
        {
            applying = true;
            try
            {
                masterVolume.normalizedValue = Mathf.Clamp01(value.x);
                musicVolume.normalizedValue = Mathf.Clamp01(value.y);
                effectsVolume.normalizedValue = Mathf.Clamp01(value.z);
            }
            finally { applying = false; }
        }

        private void OnPreviewChanged(float unused)
        {
            if (applying) return;
            PreviewChangeCount++;
            Show("LIVE PREVIEW / PRESS SAVE", CurrentNormalized());
        }
        private Vector3 CurrentNormalized() => new Vector3(masterVolume.normalizedValue, musicVolume.normalizedValue, effectsVolume.normalizedValue);
        private void Show(string state, Vector3 value)
        {
            statusLabel.text = string.Format("{0} / MASTER {1:0}%  MUSIC {2:0}%  EFFECTS {3:0}%", state, value.x * 100, value.y * 100, value.z * 100);
        }
    }
}
