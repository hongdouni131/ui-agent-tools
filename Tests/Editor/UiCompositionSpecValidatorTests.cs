using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UIAgent.Tools.Editor.Tests
{
    public class UiCompositionSpecValidatorTests
    {
        private const string GuidValue = "0123456789abcdef0123456789abcdef";

        private static JObject Anchors(bool fill = false)
        {
            return new JObject {
                ["mode"] = "anchors", ["anchorMin"] = fill ? new JArray(0, 0) : new JArray(0.5, 0.5),
                ["anchorMax"] = fill ? new JArray(1, 1) : new JArray(0.5, 0.5), ["pivot"] = new JArray(0.5, 0.5),
                ["anchoredPosition"] = new JArray(0, 0), ["sizeDelta"] = fill ? new JArray(0, 0) : new JArray(640, 400)
            };
        }

        private static JObject Layout(string mode = "vertical")
        {
            return new JObject {
                ["mode"] = mode, ["spacing"] = 16, ["padding"] = new JArray(16, 16, 16, 16),
                ["childAlignment"] = "middleCenter", ["childControlWidth"] = true, ["childControlHeight"] = true,
                ["childForceExpandWidth"] = false, ["childForceExpandHeight"] = false
            };
        }

        private static JObject Panel(string id = "Dialog")
        {
            return new JObject { ["id"] = id, ["type"] = "Panel", ["placement"] = Anchors(),
                ["childrenLayout"] = new JObject { ["mode"] = "none" }, ["style"] = new JObject { ["color"] = "#253047" },
                ["children"] = new JArray() };
        }

        private static JObject TextNode(string id, bool fill = false)
        {
            JObject node = Panel(id);
            node["type"] = "Text"; node["text"] = "Confirm"; node["placement"] = fill ? Anchors(true) : new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(560, 64) };
            node["style"] = new JObject { ["color"] = "#FFFFFF", ["fontSize"] = 32, ["alignment"] = "center" };
            return node;
        }

        private static JObject Example()
        {
            JObject root = Panel("Screen"); root["placement"] = Anchors(true);
            JObject dialog = Panel(); dialog["childrenLayout"] = Layout();
            dialog["style"] = new JObject { ["spritePath"] = "Assets/UI/RoundedPanel.png", ["spriteGuid"] = GuidValue,
                ["imageType"] = "sliced", ["border"] = new JArray(16, 16, 16, 16) };
            JObject button = Panel("ConfirmButton"); button["type"] = "Button";
            button["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(560, 80) };
            button["children"] = new JArray(TextNode("ConfirmLabel", true));
            dialog["children"] = new JArray(TextNode("Heading"), button);
            root["children"] = new JArray(dialog);
            return new JObject { ["schemaVersion"] = 2, ["screenType"] = "composition", ["targetPlatform"] = "desktop",
                ["referenceResolution"] = new JObject { ["width"] = 1920, ["height"] = 1080 },
                ["fontAssetPath"] = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset",
                ["outputPrefabPath"] = "Assets/AgentDemos/UIAgent/Prefabs/Composition/PF_Dialog.prefab", ["root"] = root };
        }

        private static JObject ConfirmationExample()
        {
            JObject spec = Example();
            JObject backdrop = Panel("Backdrop");
            backdrop["placement"] = Anchors(true);
            backdrop["style"] = new JObject { ["color"] = "#08121DCC" };
            JObject cancel = Panel("CancelButton"); cancel["type"] = "Button";
            cancel["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(560, 80) };
            cancel["children"] = new JArray(TextNode("CancelLabel", true));
            ((JArray)Dialog(spec)["children"]).Add(cancel);
            ((JArray)Root(spec)["children"]).Insert(0, backdrop);
            spec["confirmationDialog"] = new JObject {
                ["backdropId"] = "Backdrop", ["dialogPanelId"] = "Dialog",
                ["cancelButtonId"] = "CancelButton", ["confirmButtonId"] = "ConfirmButton",
                ["initialFocusId"] = "ConfirmButton"
            };
            return spec;
        }

        private static JObject Root(JObject spec) => (JObject)spec["root"];
        private static JObject Dialog(JObject spec) => (JObject)spec["root"]["children"][0];
        private static JObject Button(JObject spec) => (JObject)Dialog(spec)["children"][1];

        [Test]
        public void ConfirmationDialogIsOptionalAndValidatesItsNodeContract()
        {
            JObject ordinary = Example();
            Assert.That(UiCompositionSpecValidator.Validate(ordinary.ToString()).valid, Is.True);

            JObject modal = ConfirmationExample();
            var valid = UiCompositionSpecValidator.Validate(modal.ToString());
            Assert.That(valid.valid, Is.True, JsonConvert.SerializeObject(valid.errors));

            JArray children = (JArray)Root(modal)["children"];
            JToken first = children[0]; children[0] = children[1]; children[1] = first;
            Invalid(modal, "DIALOG_LAYER_ORDER");
        }

        [TestCase("placement", "DIALOG_BACKDROP_STRETCH")]
        [TestCase("children", "DIALOG_BACKDROP_CHILDREN")]
        public void ConfirmationDialogRejectsUnsafeBackdropShapes(string mutation, string code)
        {
            JObject modal = ConfirmationExample();
            JObject backdrop = (JObject)Root(modal)["children"][0];
            if (mutation == "placement") backdrop["placement"] = Anchors(false);
            else backdrop["children"] = new JArray(TextNode("BackdropLabel"));
            Invalid(modal, code);
        }

        private static JObject AddToggle(JObject spec)
        {
            JObject toggle = Panel("MusicToggle");
            toggle["type"] = "Toggle";
            toggle["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(320, 72) };
            toggle["toggle"] = new JObject { ["initialValue"] = true, ["labelId"] = "MusicLabel", ["indicatorId"] = "MusicIndicator" };
            JObject label = TextNode("MusicLabel", true); label["text"] = "Music"; label["placement"] = Anchors();
            JObject indicator = TextNode("MusicIndicator", true); indicator["text"] = "ON"; indicator["placement"] = Anchors();
            toggle["children"] = new JArray(label, indicator);
            ((JArray)Dialog(spec)["children"]).Add(toggle);
            return toggle;
        }

        private static void Invalid(JObject spec, string code)
        {
            var report = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(report.valid, Is.False);
            Assert.That(report.errors.Any(e => e.code == code), Is.True, JsonConvert.SerializeObject(report.errors));
            Assert.That(report.validatedSpec, Is.Null);
            Assert.That(report.specFingerprint, Is.Null);
        }

        [TestCase("1")]
        [TestCase("-6054090792327852210")]
        [TestCase("9223372036854775807")]
        [TestCase("-9223372036854775808")]
        public void SubSpriteIdsRemainExactStrings(string localId)
        {
            var spec = Example(); var style = (JObject)Dialog(spec)["style"];
            style["spriteName"] = "button_brown"; style["spriteLocalFileId"] = localId;
            var report = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(report.valid, Is.True, JsonConvert.SerializeObject(report.errors));
            Assert.That((string)report.validatedSpec["root"]["children"][0]["style"]["spriteLocalFileId"], Is.EqualTo(localId));
        }

        [TestCase("0")]
        [TestCase("-0")]
        [TestCase("01")]
        [TestCase("+1")]
        [TestCase(" 1")]
        [TestCase("1.0")]
        [TestCase("1e3")]
        [TestCase("9223372036854775808")]
        [TestCase("-9223372036854775809")]
        public void NonCanonicalOrOverflowSubSpriteIdsAreRejected(string localId)
        {
            var spec = Example(); Dialog(spec)["style"]["spriteName"] = "slice";
            Dialog(spec)["style"]["spriteLocalFileId"] = localId;
            Invalid(spec, "SPRITE_LOCAL_FILE_ID_INVALID");
        }

        [Test]
        public void NumericLocalFileIdIsRejectedBeforeAnyPrecisionCanBeLost()
        {
            var spec = Example(); Dialog(spec)["style"]["spriteName"] = "slice";
            Dialog(spec)["style"]["spriteLocalFileId"] = -6054090792327852210L;
            Invalid(spec, "SPRITE_LOCAL_FILE_ID_INVALID");
        }

        [TestCase("spriteName")]
        [TestCase("spriteLocalFileId")]
        public void SubSpriteSelectorsMustBePaired(string omitted)
        {
            var spec = Example(); var style = (JObject)Dialog(spec)["style"];
            style["spriteName"] = "slice"; style["spriteLocalFileId"] = "1"; style.Remove(omitted);
            Invalid(spec, "SPRITE_SUBASSET_FIELDS_INCOMPLETE");
        }

        [TestCase("")]
        [TestCase(" ")]
        public void SubSpriteNameCannotBeBlank(string name)
        {
            var spec = Example(); Dialog(spec)["style"]["spriteName"] = name;
            Dialog(spec)["style"]["spriteLocalFileId"] = "1"; Invalid(spec, "SPRITE_NAME_INVALID");
        }

        [Test]
        public void SubSpriteIdentityIsPartOfSpecFingerprint()
        {
            var spec = Example(); var style = (JObject)Dialog(spec)["style"];
            style["spriteName"] = "slice"; style["spriteLocalFileId"] = "1";
            var before = UiCompositionSpecValidator.Validate(spec.ToString());
            style["spriteLocalFileId"] = "2";
            var after = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(after.valid, Is.True); Assert.That(after.specFingerprint, Is.Not.EqualTo(before.specFingerprint));
        }

        [TestCase("vertical")]
        [TestCase("horizontal")]
        public void NestedSpecAcceptsExplicitLayoutAndRecordsReadOnlyBoundary(string mode)
        {
            JObject spec = Example(); Dialog(spec)["childrenLayout"]["mode"] = mode;
            var report = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(report.valid, Is.True, JsonConvert.SerializeObject(report.errors));
            Assert.That(report.nodeCount, Is.EqualTo(5));
            Assert.That(report.status, Is.EqualTo("valid"));
            Assert.That(report.projectChecksPerformed, Is.False);
            Assert.That(report.requiresConfirmation, Is.True);
            Assert.That(report.specFingerprint, Has.Length.EqualTo(64));
            Assert.That(JObject.Parse(UiCompositionSpecValidator.ValidateJson(spec.ToString()))["validationScope"].Value<string>(), Is.EqualTo("spec_only"));
        }

        [TestCase("schemaVersion")]
        [TestCase("screenType")]
        [TestCase("targetPlatform")]
        [TestCase("referenceResolution")]
        [TestCase("fontAssetPath")]
        [TestCase("outputPrefabPath")]
        [TestCase("root")]
        public void RootFieldsAreRequired(string field)
        {
            JObject spec = Example(); spec.Remove(field); Invalid(spec, "FIELD_REQUIRED");
        }

        [TestCase("id")]
        [TestCase("type")]
        [TestCase("placement")]
        [TestCase("childrenLayout")]
        [TestCase("style")]
        public void NodeFieldsAreRequired(string field)
        {
            JObject spec = Example(); Dialog(spec).Remove(field); Invalid(spec, "FIELD_REQUIRED");
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("1Dialog")]
        [TestCase("Dialog/Child")]
        [TestCase("Dialog Child")]
        [TestCase("标题")]
        public void IdsHaveAnExplicitSafeFormat(string id)
        {
            JObject spec = Example(); Dialog(spec)["id"] = id; Invalid(spec, "NODE_ID_INVALID");
        }

        [Test]
        public void DuplicateIdsAreRejectedAcrossBranchesAndUnaffectedIdsRemainStable()
        {
            JObject spec = Example(); var before = UiCompositionSpecValidator.Validate(spec.ToString());
            ((JArray)Root(spec)["children"]).Insert(0, Panel("ExtraPanel"));
            var after = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(after.valid, Is.True, JsonConvert.SerializeObject(after.errors));
            Assert.That((string)after.validatedSpec["root"]["children"][1]["id"], Is.EqualTo("Dialog"));
            Assert.That(after.specFingerprint, Is.Not.EqualTo(before.specFingerprint));
            Root(spec)["children"][0]["id"] = "ConfirmLabel"; Invalid(spec, "NODE_ID_DUPLICATE");
        }

        [TestCase("ScrollView")]
        [TestCase("Image")]
        [TestCase("panel")]
        public void ComponentWhitelistIsClosed(string type)
        {
            JObject spec = Example(); Dialog(spec)["type"] = type; Invalid(spec, "COMPONENT_UNSUPPORTED");
        }

        [Test]
        public void ToggleHasExplicitInitialValueAndTwoIdentifiedTextChildren()
        {
            JObject spec = Example();
            JObject toggle = AddToggle(spec);
            var report = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(report.valid, Is.True, JsonConvert.SerializeObject(report.errors));
            Assert.That(report.nodeCount, Is.EqualTo(8));

            toggle.Remove("toggle"); Invalid(spec, "FIELD_REQUIRED");
            toggle = AddToggle(Example()); toggle["toggle"]["initialValue"] = "true";
            InvalidWithRoot(toggle, "FIELD_TYPE");

            spec = Example(); toggle = AddToggle(spec); toggle["toggle"]["unknown"] = false;
            Invalid(spec, "FIELD_UNKNOWN");
            spec = Example(); toggle = AddToggle(spec); toggle["toggle"]["labelId"] = "NotTheLabel";
            Invalid(spec, "TOGGLE_CHILD_SHAPE");
            spec = Example(); ((JArray)Dialog(spec)["children"]).RemoveAt(1);
            Dialog(spec)["children"][0]["toggle"] = new JObject { ["initialValue"] = true,
                ["labelId"] = "A", ["indicatorId"] = "B" };
            Invalid(spec, "FIELD_UNSUPPORTED");
        }

        [Test]
        public void SliderHasBoundedRangeAndTwoIdentifiedVisualChildren()
        {
            JObject spec = Example();
            JObject slider = Panel("MusicSlider");
            slider["type"] = "Slider";
            slider["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(320, 48) };
            JObject fill = Panel("MusicFill");
            fill["placement"] = Anchors(true);
            JObject handle = Panel("MusicHandle");
            handle["placement"] = Anchors(true);
            slider["children"] = new JArray(fill, handle);
            slider["slider"] = new JObject { ["minValue"] = 0, ["maxValue"] = 100, ["initialValue"] = 65,
                ["wholeNumbers"] = false, ["fillId"] = "MusicFill", ["handleId"] = "MusicHandle" };
            ((JArray)Dialog(spec)["children"]).Add(slider);
            var report = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(report.valid, Is.True, JsonConvert.SerializeObject(report.errors));

            spec = Example(); slider = Panel("MusicSlider"); slider["type"] = "Slider";
            slider["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(320, 48) };
            fill = Panel("MusicFill"); fill["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(300, 48) };
            handle = Panel("MusicHandle"); handle["placement"] = Anchors(true);
            slider["children"] = new JArray(fill, handle);
            slider["slider"] = new JObject { ["minValue"] = 0, ["maxValue"] = 100, ["initialValue"] = 65,
                ["wholeNumbers"] = false, ["fillId"] = "MusicFill", ["handleId"] = "MusicHandle" };
            ((JArray)Dialog(spec)["children"]).Add(slider);
            Invalid(spec, "SLIDER_CHILD_SHAPE");

            slider["slider"]["maxValue"] = 0; Invalid(spec, "SLIDER_RANGE_INVALID");
            spec = Example(); slider = Panel("MusicSlider"); slider["type"] = "Slider";
            slider["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(320, 48) };
            slider["children"] = new JArray(Panel("MusicFill"), Panel("MusicHandle"));
            slider["slider"] = new JObject { ["minValue"] = 0, ["maxValue"] = 10, ["initialValue"] = 11,
                ["wholeNumbers"] = true, ["fillId"] = "MusicFill", ["handleId"] = "MusicHandle" };
            ((JArray)Dialog(spec)["children"]).Add(slider); Invalid(spec, "SLIDER_VALUE_RANGE");
        }

        private static void InvalidWithRoot(JObject node, string code)
        {
            JObject spec = Example();
            JObject previous = Dialog(spec);
            previous["children"] = new JArray(node);
            Invalid(spec, code);
        }

        [Test]
        public void SchemaAndPlatformDoNotAcceptV1OrOtherProfiles()
        {
            JObject spec = Example(); spec["schemaVersion"] = 1; Invalid(spec, "FIELD_VALUE");
            spec = Example(); spec["referenceResolution"]["width"] = 1280; Invalid(spec, "FIELD_VALUE");
            spec = Example(); spec["targetPlatform"] = "mobile"; Invalid(spec, "FIELD_VALUE");
            spec = Example(); spec["screenType"] = "main_menu"; Invalid(spec, "FIELD_VALUE");
        }

        [Test]
        public void UnknownFieldsAndTextOnContainersAreRejected()
        {
            JObject spec = Example(); Dialog(spec)["executeCode"] = "anything"; Invalid(spec, "FIELD_UNKNOWN");
            spec = Example(); Dialog(spec)["text"] = "wrong place"; Invalid(spec, "FIELD_UNSUPPORTED");
            spec = Example(); spec["referenceResolution"]["dpi"] = 96; Invalid(spec, "FIELD_UNKNOWN");
        }

        [Test]
        public void PlacementModesCannotMixAnchorAndLayoutFields()
        {
            JObject spec = Example(); Dialog(spec)["children"][0]["placement"] = Anchors(); Invalid(spec, "LAYOUT_MODE_CONFLICT");
            spec = Example(); Dialog(spec)["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(200, 200) }; Invalid(spec, "LAYOUT_MODE_CONFLICT");
            spec = Example(); Button(spec)["placement"]["anchorMin"] = new JArray(0, 0); Invalid(spec, "FIELD_UNKNOWN");
            spec = Example(); Dialog(spec)["placement"]["preferredSize"] = new JArray(200, 200); Invalid(spec, "FIELD_UNKNOWN");
        }

        [TestCase("spacing")]
        [TestCase("padding")]
        [TestCase("childAlignment")]
        [TestCase("childControlWidth")]
        [TestCase("childControlHeight")]
        [TestCase("childForceExpandWidth")]
        [TestCase("childForceExpandHeight")]
        public void LayoutDecisionsAreRequiredAndExplicit(string field)
        {
            JObject spec = Example(); ((JObject)Dialog(spec)["childrenLayout"]).Remove(field); Invalid(spec, "FIELD_REQUIRED");
        }

        [Test]
        public void LayoutSettingsAndAnchorOrderingAreChecked()
        {
            JObject spec = Example(); Dialog(spec)["childrenLayout"]["childControlWidth"] = 1; Invalid(spec, "FIELD_TYPE");
            spec = Example(); Dialog(spec)["childrenLayout"]["spacing"] = -1; Invalid(spec, "NUMBER_RANGE");
            spec = Example(); Dialog(spec)["childrenLayout"]["padding"] = new JArray(1, 2, 3.5, 4); Invalid(spec, "PADDING_INVALID");
            spec = Example(); Dialog(spec)["placement"]["anchorMin"] = new JArray(0.8, 0.5); Invalid(spec, "ANCHOR_ORDER");
            spec = Example(); Dialog(spec)["placement"]["pivot"] = new JArray(2, 0.5); Invalid(spec, "VECTOR_RANGE");
            spec = Example(); Button(spec)["placement"]["preferredSize"] = new JArray(0, 80); Invalid(spec, "VECTOR_RANGE");
            spec = Example(); Dialog(spec)["placement"]["anchoredPosition"] = new JArray(8193, 0); Invalid(spec, "VECTOR_RANGE");
        }

        [Test]
        public void ButtonHasOneFillingTextLabelAndTextIsALeaf()
        {
            JObject spec = Example(); Button(spec)["children"] = new JArray(); Invalid(spec, "BUTTON_LABEL_SHAPE");
            spec = Example(); ((JArray)Button(spec)["children"]).Add(TextNode("SecondLabel", true)); Invalid(spec, "BUTTON_LABEL_SHAPE");
            spec = Example(); Button(spec)["children"][0]["placement"]["sizeDelta"] = new JArray(10, 0); Invalid(spec, "BUTTON_LABEL_LAYOUT");
            spec = Example(); Button(spec)["children"][0]["children"] = new JArray(Panel("Nested")); Invalid(spec, "CHILDREN_UNSUPPORTED");
            spec = Example(); Button(spec)["childrenLayout"] = Layout(); Invalid(spec, "CHILDREN_LAYOUT_UNSUPPORTED");
        }

        [TestCase("")]
        [TestCase(" leading")]
        [TestCase("trailing ")]
        [TestCase("a\nb")]
        [TestCase("<b>Text</b>")]
        [TestCase("a\tb")]
        public void TextMustBePlainNonemptySingleLine(string text)
        {
            JObject spec = Example(); Button(spec)["children"][0]["text"] = text;
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.False);
        }

        [Test]
        public void TextSupportsUnicodeAndHasLengthLimit()
        {
            JObject spec = Example(); Button(spec)["children"][0]["text"] = "确定 🚀";
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True);
            Button(spec)["children"][0]["text"] = new string('A', 257); Invalid(spec, "TEXT_INVALID");
            ((JObject)Button(spec)["children"][0]).Remove("text"); Invalid(spec, "FIELD_REQUIRED");
        }

        [Test]
        public void SpriteSelectionMustBeBakedIntoSpecAndSlicedBorderIsExplicit()
        {
            JObject spec = Example(); ((JObject)Dialog(spec)["style"]).Remove("spriteGuid"); Invalid(spec, "SPRITE_GUID_REQUIRED");
            spec = Example(); Dialog(spec)["style"]["spriteGuid"] = "search-by-name"; Invalid(spec, "SPRITE_GUID_REQUIRED");
            spec = Example(); ((JObject)Dialog(spec)["style"]).Remove("border"); Invalid(spec, "SPRITE_BORDER_REQUIRED");
            spec = Example(); Dialog(spec)["style"]["border"] = new JArray(0, 0, 0, 0); Invalid(spec, "SPRITE_BORDER_EMPTY");
            spec = Example(); Dialog(spec)["style"]["border"] = new JArray(-1, 16, 16, 16); Invalid(spec, "SPRITE_BORDER_REQUIRED");
            spec = Example(); Dialog(spec)["style"]["border"] = new JArray(16.5, 16, 16, 16); Invalid(spec, "SPRITE_BORDER_REQUIRED");
            spec = Example(); Dialog(spec)["style"]["border"] = new JArray(4097, 16, 16, 16); Invalid(spec, "SPRITE_BORDER_REQUIRED");
        }

        [Test]
        public void SimpleSpriteAndPureColorHaveDistinctContracts()
        {
            JObject spec = Example(); Dialog(spec)["style"]["imageType"] = "simple"; Invalid(spec, "FIELD_UNSUPPORTED");
            ((JObject)Dialog(spec)["style"]).Remove("border");
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True);
            ((JObject)Dialog(spec)["style"]).Remove("spritePath"); Invalid(spec, "SPRITE_REQUIRED");
            Dialog(spec)["style"] = new JObject { ["color"] = "#FFFFFF80" };
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True);
            Dialog(spec)["style"] = new JObject(); Invalid(spec, "STYLE_REQUIRED");
        }

        [Test]
        public void TextEffectsAreOptionalAndHaveBoundedPairedFields()
        {
            JObject spec = Example();
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True);
            JObject style = (JObject)Button(spec)["children"][0]["style"];
            style["outlineColor"] = "#000000FF";
            style["outlineWidth"] = 0.1;
            style["shadowColor"] = "#00000080";
            style["shadowOffset"] = new JArray(0.5, -0.5);
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True);

            style["outlineWidth"] = 0.21; Invalid(spec, "NUMBER_RANGE");
            style["outlineWidth"] = 0.1; style["shadowOffset"] = new JArray(1.1, 0); Invalid(spec, "VECTOR_RANGE");
            style["shadowOffset"] = new JArray(0.5, -0.5); style["outlineColor"] = "#000"; Invalid(spec, "COLOR_INVALID");
            style["outlineColor"] = "#000000FF"; style.Remove("outlineWidth"); Invalid(spec, "OUTLINE_FIELDS_INCOMPLETE");
            style["outlineWidth"] = 0.1; style.Remove("shadowColor"); Invalid(spec, "SHADOW_FIELDS_INCOMPLETE");
        }

        [TestCase("Assets/../Outside.asset")]
        [TestCase("Assets//Font.asset")]
        [TestCase("Assets/Font.asset:stream")]
        [TestCase("Assets/Font.asset ")]
        [TestCase("Assets/CON.asset")]
        [TestCase("Packages/Font.asset")]
        [TestCase("D:/Font.asset")]
        [TestCase("Assets/%2e%2e/Font.asset")]
        public void AssetPathsAreNormalizedAndProjectRelative(string path)
        {
            JObject spec = Example(); spec["fontAssetPath"] = path; Invalid(spec, "ASSET_PATH_INVALID");
            spec = Example(); Dialog(spec)["style"]["spritePath"] = path; Invalid(spec, "ASSET_PATH_INVALID");
        }

        [TestCase("Assets/Other/UI.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/CON.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/lpt1/UI.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/../UI.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/UI.PREFAB")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/.prefab")]
        public void OutputPathsHaveAnAllowlistAndRejectReservedNames(string path)
        {
            JObject spec = Example(); spec["outputPrefabPath"] = path; Invalid(spec, "OUTPUT_PATH_INVALID");
        }

        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{} {}")]
        [TestCase("{\"x\":1,\"x\":2}")]
        [TestCase("{'x':1}")]
        [TestCase("{\"x\":1,}")]
        [TestCase("{\"x\":[1,]}")]
        [TestCase("{/*comment*/\"x\":1}")]
        [TestCase("{\"x\":NaN}")]
        [TestCase("{\"x\":Infinity}")]
        [TestCase("{\"x\":0x10}")]
        [TestCase("{\"x\":01}")]
        [TestCase("{\"x\":+1}")]
        [TestCase("{\"x\":.1}")]
        public void JsonExtensionsAndAmbiguitiesAreRejected(string json)
        {
            var report = UiCompositionSpecValidator.Validate(json);
            Assert.That(report.errors.Any(e => e.code == "JSON_INVALID"), Is.True);
        }

        [Test]
        public void InvalidTypesAnywhereDoNotThrowOrProduceFingerprint()
        {
            JObject valid = Example();
            var fields = valid.Descendants().OfType<JProperty>().Where(p => p.Value.Type != JTokenType.Array).Select(p => p.Path).ToArray();
            foreach (string path in fields)
            {
                JToken original = valid.SelectToken(path);
                string[] wrongTypes = original.Type == JTokenType.Boolean
                    ? new[] { "null", "[]", "{}", "\"not a boolean\"" }
                    : new[] { "null", "[]", "{}", "false" };
                foreach (string invalid in wrongTypes)
                {
                    JObject spec = (JObject)valid.DeepClone(); spec.SelectToken(path).Replace(JToken.Parse(invalid));
                    UiCompositionSpecValidator.Report report = null;
                    Assert.DoesNotThrow(() => report = UiCompositionSpecValidator.Validate(spec.ToString()), path + " = " + invalid);
                    Assert.That(report.valid, Is.False, path + " = " + invalid);
                    Assert.That(report.specFingerprint, Is.Null);
                }
            }
        }

        [Test]
        public void HugeAndNonFiniteNumbersCannotCrashValidation()
        {
            foreach (string value in new[] { "99999999999999999999999999999999999999" })
            {
                JObject spec = Example(); spec["schemaVersion"] = JToken.Parse(value);
                Assert.DoesNotThrow(() => Invalid(spec, "FIELD_VALUE"));
                spec = Example(); Dialog(spec)["style"]["border"] = new JArray(JToken.Parse(value), 16, 16, 16);
                Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.False);
            }
            string overflow = Example().ToString(Formatting.None).Replace("\"spacing\":16", "\"spacing\":1e309");
            Assert.DoesNotThrow(() => Assert.That(UiCompositionSpecValidator.Validate(overflow).valid, Is.False));
            Assert.That(UiCompositionSpecValidator.Validate(overflow).errors.Any(e => e.code == "JSON_INVALID"), Is.True);
        }

        [Test]
        public void NodeCountAndDepthLimitsAreRealBoundaries()
        {
            JObject spec = Example(); Root(spec)["children"] = new JArray(Enumerable.Range(0, 63).Select(i => Panel("Node_" + i)));
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True);
            ((JArray)Root(spec)["children"]).Add(Panel("TooMany")); Invalid(spec, "NODE_COUNT");
            spec = Example(); JObject node = Root(spec); node["children"] = new JArray();
            for (int i = 1; i <= 8; i++)
            {
                JObject next = Panel("Depth_" + i); node["children"] = new JArray(next); node = next;
            }
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True);
            node["children"] = new JArray(Panel("Depth_9")); Invalid(spec, "NODE_DEPTH");
        }

        [Test]
        public void EmptyOversizedAndDeepInputAreBounded()
        {
            Assert.That(UiCompositionSpecValidator.Validate(null).errors[0].code, Is.EqualTo("JSON_REQUIRED"));
            Assert.That(UiCompositionSpecValidator.Validate(new string(' ', UiCompositionSpecValidator.MaxJsonLength + 1)).errors[0].code, Is.EqualTo("JSON_TOO_LARGE"));
            Assert.That(UiCompositionSpecValidator.Validate(new string('[', 41) + "0" + new string(']', 41)).errors[0].code, Is.EqualTo("JSON_INVALID"));
        }

        private static JToken Reorder(JToken token)
        {
            if (token is JObject obj) return new JObject(obj.Properties().Reverse().Select(p => new JProperty(p.Name, Reorder(p.Value))));
            if (token is JArray array) return new JArray(array.Select(Reorder));
            return token.DeepClone();
        }

        [Test]
        public void FingerprintIgnoresPropertyOrderButTracksLayoutResourcesAndText()
        {
            JObject original = Example(); string hash = UiCompositionSpecValidator.Validate(original.ToString()).specFingerprint;
            Assert.That(UiCompositionSpecValidator.Validate(Reorder(original).ToString(Formatting.None)).specFingerprint, Is.EqualTo(hash));
            foreach (string path in new[] { "root.children[0].style.spriteGuid", "root.children[0].children[0].text", "root.children[0].childrenLayout.spacing" })
            {
                JObject spec = (JObject)original.DeepClone(); JToken token = spec.SelectToken(path);
                token.Replace(token.Type == JTokenType.Integer ? new JValue(24) : new JValue(path.EndsWith("spriteGuid") ? new string('f', 32) : "Changed"));
                var report = UiCompositionSpecValidator.Validate(spec.ToString());
                Assert.That(report.valid, Is.True, JsonConvert.SerializeObject(report.errors));
                Assert.That(report.specFingerprint, Is.Not.EqualTo(hash));
            }
        }

        [Test]
        public void ValidationCannotCreateFoldersOrDirtyTheScene()
        {
            JObject spec = Example(); string folder = "Assets/AgentDemos/UIAgent/Prefabs/CompositionTest_" + Guid.NewGuid().ToString("N");
            spec["outputPrefabPath"] = folder + "/PF_Dialog.prefab";
            var scene = SceneManager.GetActiveScene(); bool dirty = scene.isDirty; int rootCount = scene.rootCount;
            var report = UiCompositionSpecValidator.Validate(spec.ToString());
            Assert.That(report.valid, Is.True);
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
            Assert.That(scene.isDirty, Is.EqualTo(dirty)); Assert.That(scene.rootCount, Is.EqualTo(rootCount));
            Assert.That(report.limitations.Length, Is.GreaterThan(0));
            Assert.That(string.Join(" ", report.limitations), Does.Contain("Toggle and Slider provide native Unity UI state only"));
        }
    }
}
