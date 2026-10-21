using System;
using System.Collections.Generic;
using System.IO;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using ProjectHero.Logic.Grid;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Point = ProjectHero.Core.Grid.TrianglePoint;

namespace ProjectHero.EditorTools
{
    public sealed class SpatialShapeEditorWindow : EditorWindow
    {
        [SerializeField] private SpatialShapeKind shapeKind;
        [SerializeField] private string configId = "unit_volume.custom";
        [SerializeField] private List<Point> even = new List<Point>();
        [SerializeField] private List<Point> odd = new List<Point>();
        [SerializeField] private ScriptableObject source;
        [SerializeField] private int direction;
        [SerializeField] private bool initialized;
        [SerializeField] private bool draftDirty;
        [SerializeField] private UnitVolume referenceVolume;
        [SerializeField] private bool showReferenceVolume = true;
        private ShapeCanvas canvas;
        private Label status;
        private Label counts;
        private HelpBox validation;
        private ObjectField sourceField;
        private TextField idField;
        private PopupField<string> typeField;
        private SliderInt directionField;
        private ObjectField referenceField;
        private VisualElement referencePanel;
        private HelpBox referenceValidation;
        private Label savePathLabel;
        private Label legend;
        private bool eraser;
        private int presetRadius = 1;
        private string preset = "圆形";
        private int strokeGroup;
        private static readonly List<string> TypeNames = new List<string> { "单位体积", "技能影响范围" };

        [MenuItem("ProjectHero/配置工具/体积与技能范围编辑器")]
        public static void Open() => GetWindow<SpatialShapeEditorWindow>("体积与技能范围");

        [OnOpenAsset]
        private static bool OnOpenAsset(EntityId instanceId, int line)
        {
            var asset = EditorUtility.EntityIdToObject(instanceId) as ScriptableObject;
            if (!(asset is UnitVolume) && !(asset is AttackPattern)) return false;
            var window = GetWindow<SpatialShapeEditorWindow>("体积与技能范围");
            window.LoadAsset(asset);
            return true;
        }

        private void OnEnable()
        {
            minSize = new Vector2(900, 620);
            if (!initialized)
            {
                even = MakePreset("圆形", 1);
                odd = new List<Point>(even);
                initialized = true;
            }
            hasUnsavedChanges = draftDirty;
            saveChangesMessage = "范围配置尚未保存为 Unity 资产。";
            if (!draftDirty && referenceVolume == null && source is AttackPattern pattern)
                referenceVolume = FindPatternVolume(pattern);
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this)));
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(folder + "/SpatialShapeEditor.uss");
            if (sheet != null) root.styleSheets.Add(sheet);
            root.AddToClassList("shape-editor");
            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarButton(NewDraft) { text = "新建配置" });
            toolbar.Add(new ToolbarButton(() => SaveAsset(false)) { text = "保存资产" });
            toolbar.Add(new ToolbarButton(() => SaveAsset(true)) { text = "另存为…" });
            toolbar.Add(new ToolbarButton(ImportJson) { text = "导入 JSON" });
            toolbar.Add(new ToolbarButton(ExportJson) { text = "导出 JSON" });
            toolbar.Add(new ToolbarButton(() => Undo.PerformUndo()) { text = "撤销" });
            toolbar.Add(new ToolbarButton(() => Undo.PerformRedo()) { text = "重做" });
            root.Add(toolbar);

            var body = new VisualElement();
            body.AddToClassList("body");
            root.Add(body);
            var sidebar = new ScrollView();
            sidebar.AddToClassList("sidebar");
            body.Add(sidebar);
            sidebar.Add(Heading("配置"));
            sourceField = new ObjectField("现有资产") { objectType = typeof(ScriptableObject), allowSceneObjects = false };
            sourceField.SetValueWithoutNotify(source);
            sourceField.RegisterValueChangedCallback(e =>
            {
                if (e.newValue is UnitVolume || e.newValue is AttackPattern) LoadAsset((ScriptableObject)e.newValue);
                else if (e.newValue != null) Notify("请选择 UnitVolume 或 AttackPattern 资产。");
                sourceField.SetValueWithoutNotify(source);
            });
            sidebar.Add(sourceField);
            sidebar.Add(new Button(() => LoadAsset(Selection.activeObject as ScriptableObject)) { text = "载入 Project 中选中的资产" });
            typeField = new PopupField<string>("配置类型", TypeNames, (int)shapeKind);
            typeField.RegisterValueChangedCallback(e =>
            {
                Record("更改配置类型");
                shapeKind = (SpatialShapeKind)TypeNames.IndexOf(e.newValue);
                source = null;
                sourceField.SetValueWithoutNotify(null);
                Changed(); canvas.Fit();
            });
            sidebar.Add(typeField);
            idField = new TextField("配置 ID") { value = configId, isDelayed = true };
            idField.RegisterValueChangedCallback(e => { Record("更改配置 ID"); configId = e.newValue; Changed(); });
            sidebar.Add(idField);
            sidebar.Add(Description("资产保存兼容现有 UnitVolume / AttackPattern；JSON 包含完整的 12 向整数坐标。"));
            savePathLabel = Description("");
            sidebar.Add(savePathLabel);

            referencePanel = new VisualElement();
            sidebar.Add(referencePanel);
            referencePanel.Add(Heading("适配单位体积"));
            referenceField = new ObjectField("单位体积") { objectType = typeof(UnitVolume), allowSceneObjects = false };
            referenceField.SetValueWithoutNotify(referenceVolume);
            referenceField.RegisterValueChangedCallback(e =>
            {
                Record("选择适配单位体积"); referenceVolume = e.newValue as UnitVolume;
                Changed(); canvas.Fit();
            });
            referencePanel.Add(referenceField);
            var showReference = new Toggle("显示单位体积") { value = showReferenceVolume };
            showReference.RegisterValueChangedCallback(e => { showReferenceVolume = e.newValue; Refresh(); });
            referencePanel.Add(showReference);
            referencePanel.Add(Description("蓝色为单位体积，橙色为技能范围；重叠格保留蓝色描边。两者共用锚点，并同步切换朝向。"));
            referenceValidation = new HelpBox("请选择制作此技能范围时对照的单位体积。", HelpBoxMessageType.Info);
            referencePanel.Add(referenceValidation);

            sidebar.Add(Heading("朝向与画笔"));
            directionField = new SliderInt("朝向（每档 30°）", 0, 11) { value = direction, showInputField = true };
            directionField.RegisterValueChangedCallback(e => { direction = Mathf.Clamp(e.newValue, 0, 11); Refresh(); });
            sidebar.Add(directionField);
            var bases = Row(sidebar);
            bases.Add(new Button(() => SetDirection(0)) { text = "编辑 0° 基准" });
            bases.Add(new Button(() => SetDirection(1)) { text = "编辑 30° 基准" });
            var brush = new PopupField<string>("画笔", new List<string> { "涂选", "擦除" }, 0);
            brush.RegisterValueChangedCallback(e => eraser = e.newValue == "擦除");
            sidebar.Add(brush);
            counts = new Label();
            counts.AddToClassList("description");
            sidebar.Add(counts);
            sidebar.Add(Description("左键拖动绘制，Shift 或右键擦除；中键拖动平移，滚轮缩放。黄色十字是单位锚点，箭头表示朝向。"));
            sidebar.Add(new Button(() => canvas.Fit()) { text = "适配视图 / 回到原点" });

            sidebar.Add(Heading("快速形状"));
            var presetField = new PopupField<string>("形状", new List<string> { "圆形", "前方扇形", "直线" }, 0);
            presetField.RegisterValueChangedCallback(e => preset = e.newValue);
            sidebar.Add(presetField);
            var radiusField = new SliderInt("半径 / 长度", 1, 8) { value = 1, showInputField = true };
            radiusField.RegisterValueChangedCallback(e => presetRadius = Mathf.Clamp(e.newValue, 1, 8));
            sidebar.Add(radiusField);
            sidebar.Add(new Button(ApplyPreset) { text = "替换当前基准" });
            sidebar.Add(new Button(() =>
            {
                if (!CanEdit()) return;
                Record("清空基准"); Current.Clear(); Changed();
            }) { text = "清空当前基准" });
            sidebar.Add(Description("0° 和 30° 必须分别制作。其余 10 个方向只读，由受检整数旋转生成。"));
            validation = new HelpBox("", HelpBoxMessageType.Info);
            sidebar.Add(validation);

            var workspace = new VisualElement();
            workspace.AddToClassList("workspace");
            body.Add(workspace);
            var title = new Label("三角网格 · 相对单位锚点 (0, 0)");
            title.AddToClassList("canvas-title");
            workspace.Add(title);
            legend = new Label();
            legend.AddToClassList("canvas-legend");
            workspace.Add(legend);
            canvas = new ShapeCanvas
            {
                BeginStroke = () => { if (!CanEdit()) return false; Record("绘制范围"); strokeGroup = Undo.GetCurrentGroup(); return true; },
                Paint = Paint,
                EndStroke = () => Undo.CollapseUndoOperations(strokeGroup),
                Hover = p => Notify(p.HasValue ? $"三角格 ({p.Value.X}, {p.Value.Y}, {p.Value.T})" : "" )
            };
            workspace.Add(canvas);
            status = new Label("就绪");
            status.AddToClassList("status");
            root.Add(status);
            Refresh();
            canvas.RegisterCallback<GeometryChangedEvent>(_ => { if (!canvas.HasFitted) canvas.Fit(); });
        }

        private static Label Heading(string text)
        {
            var label = new Label(text); label.AddToClassList("heading"); return label;
        }

        private static Label Description(string text)
        {
            var label = new Label(text); label.AddToClassList("description"); return label;
        }

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row;
        }

        private List<Point> Current => direction == 1 ? odd : even;
        private void Record(string name) { Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName(name); Undo.RegisterCompleteObjectUndo(this, name); }
        private void Changed() { draftDirty = true; hasUnsavedChanges = true; Refresh(); }
        private void OnUndoRedo()
        {
            draftDirty = true; hasUnsavedChanges = true;
            sourceField?.SetValueWithoutNotify(source);
            idField?.SetValueWithoutNotify(configId);
            typeField?.SetValueWithoutNotify(TypeNames[(int)shapeKind]);
            directionField?.SetValueWithoutNotify(direction);
            referenceField?.SetValueWithoutNotify(referenceVolume);
            Refresh();
        }

        private void SetDirection(int value) { direction = value; directionField.SetValueWithoutNotify(value); Refresh(); }
        private bool CanEdit()
        {
            if (direction <= 1) return true;
            Notify("当前是生成朝向，只读。请切换到 0° 或 30° 基准进行绘制。"); return false;
        }

        private void Paint(Point point, bool erase)
        {
            bool remove = erase || eraser;
            bool contains = Current.Contains(point);
            if (remove && contains) { Current.RemoveAll(p => p == point); Changed(); }
            else if (!remove && !contains) { Current.Add(point); Changed(); }
        }

        private void Refresh()
        {
            if (canvas == null) return;
            var points = new List<Point>();
            string error = null;
            try
            {
                var table = SpatialShapeConfiguration.Expand(even, odd);
                points = SpatialShapeConfiguration.ToLegacy(table[direction].Triangles);
                if (string.IsNullOrWhiteSpace(configId)) error = "请填写配置 ID。";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (direction <= 1) points = new List<Point>(Current);
            }
            canvas.SetShape(points, direction, shapeKind == SpatialShapeKind.UnitVolume);
            bool isAttack = shapeKind == SpatialShapeKind.AttackPattern;
            referencePanel.EnableInClassList("hidden", !isAttack);
            var volumePoints = new List<Point>();
            string referenceError = null;
            if (isAttack && referenceVolume != null)
            {
                try
                {
                    var volume = LegacyGridDefinitionConverter.ConvertVolume("editor.reference", referenceVolume);
                    volumePoints = SpatialShapeConfiguration.ToLegacy(volume.Directions[direction].Triangles);
                }
                catch (Exception ex) { referenceError = "单位体积无法预览：" + ex.Message; }
            }
            canvas.SetReferenceVolume(isAttack && showReferenceVolume ? volumePoints : new List<Point>());
            referenceValidation.text = referenceError ?? (referenceVolume == null
                ? "请选择制作此技能范围时对照的单位体积。"
                : $"{referenceVolume.name} · 当前朝向 {volumePoints.Count} 格（只读参照）");
            referenceValidation.messageType = referenceError == null ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning;
            legend.text = isAttack
                ? $"橙色：技能范围   蓝色描边：单位体积{(referenceVolume != null ? " · " + referenceVolume.name : "（未选择）")}{(!showReferenceVolume ? "（已隐藏）" : "") }"
                : "蓝色：单位体积   黄色：锚点与朝向";
            savePathLabel.text = source == null ? "默认保存目录：\n" + DefaultAssetFolder(shapeKind)
                : "当前保存位置：\n" + AssetDatabase.GetAssetPath(source);
            counts.text = $"0°：{even.Count} 格   |   30°：{odd.Count} 格\n当前 {direction * 30}°：{points.Count} 格 · {(direction <= 1 ? "可编辑" : "只读预览")}";
            validation.text = error == null ? "校验通过：两套基准可展开为完整 12 向配置。" : "暂不可保存：" + error;
            validation.messageType = error == null ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning;
        }

        private void Notify(string text) { if (status != null) status.text = text; }
        private bool ConfirmReplace() => !draftDirty || EditorUtility.DisplayDialog("未保存的范围配置", "当前草稿尚未保存，是否放弃并载入新配置？", "放弃草稿", "取消");

        private void NewDraft()
        {
            if (!ConfirmReplace()) return;
            Record("新建范围配置");
            source = null; even = MakePreset("圆形", 1); odd = new List<Point>(even);
            configId = shapeKind == SpatialShapeKind.UnitVolume ? "unit_volume.custom" : "pattern.custom";
            direction = 0; draftDirty = true; hasUnsavedChanges = true;
            CreateGUI();
        }

        private void LoadAsset(ScriptableObject asset)
        {
            if (!(asset is UnitVolume) && !(asset is AttackPattern)) { Notify("请选择 UnitVolume 或 AttackPattern。"); return; }
            if (!ConfirmReplace()) return;
            try
            {
                List<Point> nextEven, nextOdd;
                if (asset is AttackPattern pattern)
                {
                    nextEven = new List<Point>(pattern.RelativeTriangles ?? new List<Point>());
                    nextOdd = new List<Point>(pattern.RelativeTrianglesOdd ?? new List<Point>());
                }
                else
                {
                    var volume = (UnitVolume)asset;
                    nextEven = ReadBase(volume, 0); nextOdd = ReadBase(volume, 1);
                }
                // Do not silently discard explicit non-base directions that disagree with the canonical table.
                if (asset is UnitVolume v && nextEven.Count > 0 && nextOdd.Count > 0)
                    LegacyGridDefinitionConverter.ConvertVolume("editor.validation", v);
                Record("载入范围配置");
                even = nextEven; odd = nextOdd; source = asset;
                shapeKind = asset is UnitVolume ? SpatialShapeKind.UnitVolume : SpatialShapeKind.AttackPattern;
                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
                configId = shapeKind == SpatialShapeKind.UnitVolume
                    ? LegacyIdMigrationManifest.FindVolume(guid)?.FinalVolumeSpecId ?? asset.name
                    : LegacyIdMigrationManifest.FindPattern(guid)?.FinalAttackPatternId ?? asset.name;
                string savedId = asset is UnitVolume savedVolume ? savedVolume.ConfigurationId : ((AttackPattern)asset).ConfigurationId;
                if (!string.IsNullOrWhiteSpace(savedId)) configId = savedId;
                referenceVolume = asset is AttackPattern loadedPattern ? FindPatternVolume(loadedPattern) : (UnitVolume)asset;
                direction = 0; draftDirty = false; hasUnsavedChanges = false;
                CreateGUI(); Notify("已载入 " + AssetDatabase.GetAssetPath(asset));
            }
            catch (Exception ex) { Notify("载入失败：" + ex.Message); }
        }

        private static List<Point> ReadBase(UnitVolume volume, int index)
        {
            if (volume.Volumes == null) return new List<Point>();
            var found = volume.Volumes.FindAll(v => v != null && (int)v.Direction == index);
            if (found.Count > 1) throw new ArgumentException($"朝向 {index} 存在重复定义，请先在 Inspector 中清理。");
            return found.Count == 0 ? new List<Point>() : new List<Point>(found[0].RelativeTriangles ?? new List<Point>());
        }

        private bool SaveAsset(bool saveAs)
        {
            ScriptableObject target = null;
            bool isNew = false;
            try
            {
                var config = BuildConfiguration();
                target = saveAs ? null : source;
                if (target == null)
                {
                    string folder = DefaultAssetFolder(shapeKind);
                    EnsureAssetFolder(folder);
                    string path = EditorUtility.SaveFilePanelInProject("保存范围配置", configId, "asset", "选择配置保存位置", folder);
                    if (string.IsNullOrEmpty(path)) return false;
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null) throw new ArgumentException("此路径已有资产，请选择新路径，或载入该资产后保存。");
                    target = shapeKind == SpatialShapeKind.UnitVolume ? (ScriptableObject)CreateInstance<UnitVolume>() : CreateInstance<AttackPattern>();
                    isNew = true;
                    config.ApplyTo(target, referenceVolume);
                    AssetDatabase.CreateAsset(target, path);
                }
                else
                {
                    Undo.RecordObject(target, "保存范围配置");
                    config.ApplyTo(target, referenceVolume);
                    EditorUtility.SetDirty(target);
                }
                AssetDatabase.SaveAssetIfDirty(target);
                source = target; sourceField.SetValueWithoutNotify(source);
                draftDirty = false; hasUnsavedChanges = false;
                EditorGUIUtility.PingObject(target);
                Notify("已保存 " + AssetDatabase.GetAssetPath(target));
                Refresh();
                return true;
            }
            catch (Exception ex)
            {
                if (isNew && target != null && !AssetDatabase.Contains(target)) DestroyImmediate(target);
                Notify("保存失败：" + ex.Message); return false;
            }
        }

        public override void SaveChanges() { if (SaveAsset(false)) base.SaveChanges(); }
        public override void DiscardChanges() { draftDirty = false; base.DiscardChanges(); }

        private void ExportJson()
        {
            try
            {
                var config = BuildConfiguration();
                string folder = DefaultAssetFolder(shapeKind);
                EnsureAssetFolder(folder);
                string path = EditorUtility.SaveFilePanel("导出 12 向配置", Path.GetFullPath(folder), configId + ".json", "json");
                if (string.IsNullOrEmpty(path)) return;
                File.WriteAllText(path, JsonUtility.ToJson(config, true));
                AssetDatabase.Refresh(); Notify("已导出 " + path);
            }
            catch (Exception ex) { Notify("导出失败：" + ex.Message); }
        }

        private void ImportJson()
        {
            string path = EditorUtility.OpenFilePanel("导入范围配置", Application.dataPath, "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var config = SpatialShapeConfiguration.Parse(File.ReadAllText(path));
                if (!ConfirmReplace()) return;
                Record("导入范围 JSON");
                shapeKind = config.Validate(); configId = config.id;
                even = new List<Point>(config.directions[0].triangles); odd = new List<Point>(config.directions[1].triangles);
                referenceVolume = FindVolumeById(config.referenceVolumeId);
                source = null; direction = 0; draftDirty = true; hasUnsavedChanges = true;
                CreateGUI();
                Notify(!string.IsNullOrEmpty(config.referenceVolumeId) && referenceVolume == null
                    ? "范围已导入；未找到对应单位体积，请重新选择：" + config.referenceVolumeId
                    : "已导入；保存资产后即可在项目中引用。");
            }
            catch (Exception ex) { Notify("导入失败，草稿未修改：" + ex.Message); }
        }

        private void ApplyPreset()
        {
            if (!CanEdit()) return;
            Record("生成范围形状");
            var points = MakePreset(preset, presetRadius, direction * 30);
            if (direction == 0) even = points; else odd = points;
            Changed(); canvas.Fit();
        }

        internal static string DefaultAssetFolder(SpatialShapeKind kind) => "Assets/Resources/" +
            (kind == SpatialShapeKind.UnitVolume ? LegacyAssetIdentity.UnitVolumeFolder : LegacyAssetIdentity.GeneratedActionsFolder);

        private static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private SpatialShapeConfiguration BuildConfiguration()
        {
            if (shapeKind == SpatialShapeKind.AttackPattern && referenceVolume != null)
                LegacyGridDefinitionConverter.ConvertVolume("editor.reference", referenceVolume);
            return SpatialShapeConfiguration.Create(configId, shapeKind, even, odd,
                referenceVolume != null ? VolumeId(referenceVolume) : null);
        }

        private static string VolumeId(UnitVolume volume)
        {
            if (!string.IsNullOrWhiteSpace(volume.ConfigurationId)) return volume.ConfigurationId;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(volume));
            return LegacyIdMigrationManifest.FindVolume(guid)?.FinalVolumeSpecId ?? volume.name;
        }

        private static UnitVolume FindVolumeById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            UnitVolume result = null;
            foreach (string guid in AssetDatabase.FindAssets("t:UnitVolume"))
            {
                var volume = AssetDatabase.LoadAssetAtPath<UnitVolume>(AssetDatabase.GUIDToAssetPath(guid));
                if (volume == null || VolumeId(volume) != id) continue;
                if (result != null) return null;
                result = volume;
            }
            return result;
        }

        private static UnitVolume FindPatternVolume(AttackPattern pattern)
        {
            if (pattern.AuthoringVolume != null) return pattern.AuthoringVolume;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(pattern));
            UnitVolume result = null;
            foreach (var action in LegacyIdMigrationManifest.Actions)
            {
                if (action.PatternAssetGuid != guid) continue;
                foreach (var binding in LegacyIdMigrationManifest.LibraryVolumes)
                {
                    if (binding.LibraryGuid != action.Key.AssetGuid) continue;
                    var volume = AssetDatabase.LoadAssetAtPath<UnitVolume>(AssetDatabase.GUIDToAssetPath(binding.VolumeGuid));
                    if (result != null && result != volume) return null;
                    result = volume;
                }
            }
            return result;
        }

        private static List<Point> MakePreset(string shape, int radius, float angle = 0)
        {
            var points = new List<Point>();
            var forward = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
            for (int x = -radius * 2 - 1; x <= radius * 2 + 1; x++)
            for (int y = -radius * 2 - 1; y <= radius * 2 + 1; y++)
            for (int t = -1; t <= 1; t += 2)
            {
                if (!ProjectHero.Logic.Grid.TrianglePoint.IsValid(x, y, t)) continue;
                var center = new Vector2(x * .5f, (y + t / 3f) * .8660254f);
                if (center.magnitude > radius + .001f) continue;
                float along = Vector2.Dot(center, forward);
                float across = Mathf.Abs(center.x * forward.y - center.y * forward.x);
                if (shape == "前方扇形" && along < center.magnitude * .5f) continue;
                if (shape == "直线" && (along < 0 || across > .51f)) continue;
                points.Add(new Point(x, y, t));
            }
            return points;
        }
    }
}
