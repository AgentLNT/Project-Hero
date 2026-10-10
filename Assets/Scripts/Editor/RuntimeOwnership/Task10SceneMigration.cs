using System;
using System.Collections.Generic;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.UnityView;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectHero.Editor.RuntimeOwnership
{
    public static class Task10SceneMigration
    {
        public static void WireMainScene()
        {
            var scene = EditorSceneManager.OpenScene(RuntimeOwnershipSceneTool.MainScenePath, OpenSceneMode.Single);
            var bootstrap = Find<BattleRuntimeBootstrap>(scene);
            var factory = Find<BattleSimulationSourceFactory>(scene);
            if (bootstrap == null || factory == null) throw new InvalidOperationException("MAIN_SCENE_INITIALIZATION_CHAIN_MISSING");
            var existing = Find<BattlePresentationView>(scene);
            if (existing == null) existing = CreatePresentation(scene, bootstrap, factory);
            RefineLayout(existing);
            var serialized = new SerializedObject(bootstrap);
            serialized.FindProperty("_viewConsumerSlot").objectReferenceValue = existing;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // Wiring is independent of cutover. Do not change the mode until full Shadow evidence passes.
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        }
        private static void RefineLayout(BattlePresentationView presentation)
        {
            var canvas = presentation.GetComponentInChildren<Canvas>(true);
            if (canvas == null) throw new InvalidOperationException("PRESENTATION_CANVAS_MISSING");
            void Place(string name, Vector2 min, Vector2 max)
            {
                var rect = canvas.transform.Find(name) as RectTransform;
                if (rect == null) throw new InvalidOperationException("PRESENTATION_PANEL_MISSING:" + name);
                rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            }
            Place("Status", new Vector2(.02f, .82f), new Vector2(.32f, .99f));
            Place("Actions", new Vector2(.78f, .55f), new Vector2(.99f, .99f));
            Place("Preview", new Vector2(.78f, .33f), new Vector2(.99f, .54f));
            Place("Reactions", new Vector2(.78f, .08f), new Vector2(.99f, .32f));
            Place("Timeline", new Vector2(.02f, .08f), new Vector2(.76f, .25f));
            Place("EventDebug", new Vector2(.02f, .26f), new Vector2(.32f, .41f));
            Place("Controls", new Vector2(.02f, .01f), new Vector2(.99f, .065f));
            Place("DeveloperReplay", new Vector2(.34f, .42f), new Vector2(.76f, .75f));
            var controls = canvas.transform.Find("Controls").GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            controls.childControlHeight = true; controls.childForceExpandHeight = true;
            foreach (var button in canvas.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                var label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.alignment = TextAlignmentOptions.Midline;
            }
        }
        private static BattlePresentationView CreatePresentation(Scene scene, BattleRuntimeBootstrap bootstrap, BattleSimulationSourceFactory factory)
        {
            var root = new GameObject("NewBattlePresentation"); SceneManager.MoveGameObjectToScene(root, scene);
            var grid = root.AddComponent<GridView>(); var registry = root.AddComponent<BattleViewRegistry>();
            var presentation = root.AddComponent<BattlePresentationView>();
            var source = new SerializedObject(factory);
            var views = new List<CombatUnitView>();
            foreach (var pair in new[] { ("_heroUnit", "hero"), ("_enemyUnit", "enemy") })
            {
                var legacy = source.FindProperty(pair.Item1).objectReferenceValue as Component;
                if (legacy == null) throw new InvalidOperationException("SCENE_SLOT_SOURCE_MISSING:" + pair.Item2);
                var view = legacy.GetComponent<CombatUnitView>() ?? legacy.gameObject.AddComponent<CombatUnitView>();
                view.Configure(pair.Item2, grid, legacy.transform.position.y); views.Add(view);
                var body = legacy.GetComponentInChildren<MeshRenderer>();
                view.ConfigureBody(body != null ? body.transform : null, legacy.GetComponentInChildren<Animator>());
            }
            registry.Configure(views.ToArray());
            var camera = Find<Camera>(scene);
            var legacyCanvases = new List<GameObject>();
            foreach (var sceneRoot in scene.GetRootGameObjects())
                foreach (var canvas in sceneRoot.GetComponentsInChildren<Canvas>(true)) legacyCanvases.Add(canvas.gameObject);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (font == null) throw new InvalidOperationException("PRESENTATION_FONT_MISSING");
            var canvasGo = new GameObject("NewBattleCanvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            canvasGo.transform.SetParent(root.transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.GetComponent<Canvas>().sortingOrder = 10;
            var scaler = canvasGo.GetComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1980, 1080); scaler.matchWidthOrHeight = .5f;
            var status = Text(Panel(canvasGo.transform, "Status", new Vector2(.02f, .77f), new Vector2(.63f, .99f)), "Battle status", font, 22);
            var actionsPanel = Panel(canvasGo.transform, "Actions", new Vector2(.67f, .58f), new Vector2(.99f, .99f));
            var actionColumns = new GameObject("ActionColumns", typeof(RectTransform), typeof(UnityEngine.UI.HorizontalLayoutGroup));
            actionColumns.transform.SetParent(actionsPanel, false); Stretch((RectTransform)actionColumns.transform);
            var h = actionColumns.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>(); h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = true; h.childForceExpandHeight = true; h.spacing = 10;
            var actions = List(actionColumns.transform, "Actions"); var targets = List(actionColumns.transform, "Targets");
            var timeline = Scroll(Panel(canvasGo.transform, "Timeline", new Vector2(.02f, .02f), new Vector2(.63f, .30f)));
            var reactions = Scroll(Panel(canvasGo.transform, "Reactions", new Vector2(.67f, .02f), new Vector2(.99f, .28f)));
            var preview = Text(Panel(canvasGo.transform, "Preview", new Vector2(.67f, .35f), new Vector2(.99f, .57f)), "Action preview", font, 18);
            var debug = Text(Panel(canvasGo.transform, "EventDebug", new Vector2(.02f, .31f), new Vector2(.63f, .47f)), "Committed event details", font, 13);
            var controls = Panel(canvasGo.transform, "Controls", new Vector2(.67f, .29f), new Vector2(.99f, .34f));
            var controlsLayout = controls.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>(); controlsLayout.childControlWidth = true; controlsLayout.childForceExpandWidth = true; controlsLayout.spacing = 4;
            var confirm = Button(controls, "Confirm", font); var cancel = Button(controls, "Cancel", font);
            var close = Button(controls, "End turn", font); var concurrent = Button(controls, "Concurrent", font);
            var pause = Button(controls, "Pause", font); var rotate = Button(controls, "Turn", font);
            var actionToggle = Button(controls, "Actions", font);
            var replayToggle = Button(controls, "Replay", font);
            var replayPanel = Panel(canvasGo.transform, "DeveloperReplay", new Vector2(.08f, .48f), new Vector2(.59f, .75f));
            var pathPanel = Panel(replayPanel, "FilePath", new Vector2(.02f, .78f), new Vector2(.98f, .98f));
            var pathText = Text(pathPanel, "", font, 16);
            pathText.raycastTarget = true;
            var path = pathPanel.gameObject.AddComponent<TMP_InputField>();
            path.textViewport = pathPanel; path.textComponent = (TextMeshProUGUI)pathText;
            var replayStatus = Text(Panel(replayPanel, "PlaybackStatus", new Vector2(.02f, .23f), new Vector2(.98f, .76f)), "No replay loaded", font, 16);
            var replayButtons = Panel(replayPanel, "PlaybackControls", new Vector2(.02f, .02f), new Vector2(.98f, .21f));
            var replayLayout = replayButtons.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            replayLayout.childControlWidth = true; replayLayout.childControlHeight = true; replayLayout.childForceExpandWidth = true; replayLayout.spacing = 5;
            var saveReplay = Button(replayButtons, "Save", font); var loadReplay = Button(replayButtons, "Load", font);
            var playReplay = Button(replayButtons, "Play", font); var pauseReplay = Button(replayButtons, "Pause", font);
            var restartReplay = Button(replayButtons, "From start", font); var speedReplay = Button(replayButtons, "Speed", font);
            var legacyPresentation = new List<Behaviour>();
            foreach (var sceneRoot in scene.GetRootGameObjects())
                foreach (var component in sceneRoot.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component != null && component.GetType().Namespace != null
                        && (component.GetType().Namespace.StartsWith("ProjectHero.UI", StringComparison.Ordinal)
                        || component.GetType().Namespace.StartsWith("ProjectHero.Visuals", StringComparison.Ordinal))) legacyPresentation.Add(component);
            presentation.Configure(bootstrap, registry, grid, camera, "controller.player", canvasGo, actions, targets, timeline, reactions,
                status, preview, debug, font, legacyPresentation.ToArray(), confirm, cancel, close, concurrent, pause, rotate);
            presentation.ConfigureLegacyCanvases(legacyCanvases.ToArray()); presentation.ConfigureActionToggle(actionToggle);
            presentation.ConfigureReplay(replayPanel.gameObject, replayToggle, path, replayStatus, saveReplay, loadReplay,
                playReplay, pauseReplay, restartReplay, speedReplay);
            replayPanel.gameObject.SetActive(false);
            var audio = root.AddComponent<AudioSource>(); audio.playOnAwake = false; audio.spatialBlend = 0;
            var feedback = root.AddComponent<BattleFeedbackPlayer>(); feedback.Configure(registry, camera, audio); presentation.ConfigureFeedback(feedback);
            var footprintGo = new GameObject("LogicFootprints", typeof(MeshFilter), typeof(MeshRenderer), typeof(BattleFootprintView));
            footprintGo.transform.SetParent(root.transform, false);
            const string footprintMaterialPath = "Assets/Materials/NewBattleFootprints.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(footprintMaterialPath);
            if (material == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) throw new InvalidOperationException("FOOTPRINT_SHADER_MISSING");
                material = new Material(shader); material.SetColor("_BaseColor", new Color(0, 1, 0, .3f));
                material.SetFloat("_Surface", 1); material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); material.SetFloat("_ZWrite", 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.SetOverrideTag("RenderType", "Transparent"); material.renderQueue = 3000;
                AssetDatabase.CreateAsset(material, footprintMaterialPath);
            }
            var footprints = footprintGo.GetComponent<BattleFootprintView>();
            footprints.Configure(grid, footprintGo.GetComponent<MeshFilter>(), footprintGo.GetComponent<MeshRenderer>(), material);
            presentation.ConfigureFootprints(footprints);
            canvasGo.SetActive(false);
            return presentation;
        }
        private static RectTransform Panel(Transform parent, string name, Vector2 min, Vector2 max)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image)); go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            go.GetComponent<UnityEngine.UI.Image>().color = new Color(.025f, .045f, .07f, .9f); go.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            return rect;
        }
        private static RectTransform List(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.VerticalLayoutGroup)); go.transform.SetParent(parent, false);
            var v = go.GetComponent<UnityEngine.UI.VerticalLayoutGroup>(); v.spacing = 4; v.padding = new RectOffset(8, 8, 8, 8);
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            return (RectTransform)go.transform;
        }
        private static RectTransform Scroll(RectTransform parent)
        {
            var scroll = parent.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            var viewport = Panel(parent, "Viewport", Vector2.zero, Vector2.one); viewport.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            viewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = false;
            var content = List(viewport, "Content"); content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(.5f, 1); content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            return content;
        }
        private static TMP_Text Text(Transform parent, string text, TMP_FontAsset font, int size)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(parent, false); Stretch((RectTransform)go.transform);
            var label = go.GetComponent<TextMeshProUGUI>(); label.font = font; label.fontSize = size; label.enableAutoSizing = false; label.raycastTarget = false; label.text = text;
            return label;
        }
        private static UnityEngine.UI.Button Button(Transform parent, string text, TMP_FontAsset font)
        {
            var go = new GameObject(text, typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button)); go.transform.SetParent(parent, false);
            go.GetComponent<UnityEngine.UI.Image>().color = new Color(.12f, .19f, .27f, 1); Text(go.transform, text, font, 15);
            return go.GetComponent<UnityEngine.UI.Button>();
        }
        private static void Stretch(RectTransform rect) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(8, 4); rect.offsetMax = new Vector2(-8, -4); }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } return null; }
    }
}
