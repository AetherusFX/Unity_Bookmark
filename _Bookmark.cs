/*
@name: _Bookmark
@version: 1.4.1

Copyright (c) 2025 AetherusFX

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using System.Linq;
using System.IO;
using System;
using Object = UnityEngine.Object;
using System.Text;


public class _Bookmark : EditorWindow
{
    public enum AssetGroupType { Material, Texture, Mesh, Prefab, Scene, Shader }

    // ===== 셰이더 1차 분류 설정 (JSON에 저장하지 않습니다) =====
    private static readonly ShaderCategoryRule[] ShaderCategories =
    {
        new ShaderCategoryRule("UI", pathSegments: new[] { "UI" }),
        new ShaderCategoryRule("Mobile", pathSegments: new[] { "Mobile" },
        prefixes: new[] { "Legacy Shaders/Particles/" }),
        new ShaderCategoryRule("ETC", catchAll: true),
        // 추가 예시: ETC 앞에 아래 규칙을 넣으면 4번째 분류로 분리됩니다.
        // new ShaderCategoryRule("Custom", pathSegments: new[] { "Custom" }),
    };

    private sealed class ShaderCategoryRule
    {
        public readonly string Name;
        public readonly string[] PathSegments;
        public readonly string[] Prefixes;
        public readonly bool CatchAll;

        public ShaderCategoryRule(string name, string[] pathSegments = null, string[] prefixes = null, bool catchAll = false)
        {
            Name = name;
            PathSegments = pathSegments ?? new string[0];
            Prefixes = prefixes ?? new string[0];
            CatchAll = catchAll;
        }
    }
    // ===== 분류 설정 끝 =====

    private readonly string[] _mainHeaders = new[] { "Project Finder", "Settings" };

    private const string RedXTagName = "사용X";

    [System.Serializable] public class SerializableColor { public float r, g, b, a; public SerializableColor() { r = g = b = a = 1f; } public SerializableColor(Color c) { r = c.r; g = c.g; b = c.b; a = c.a; } public Color ToColor() => new Color(r, g, b, a); }
    [System.Serializable] public class FavoriteAsset { public string guid; public List<string> tags = new(); }
    [System.Serializable] public class FavoriteGroup { public AssetGroupType groupType; public List<FavoriteAsset> assets = new(); }
    [System.Serializable] public class TagInfo { public string name; public SerializableColor color = new(Color.gray); }
    [System.Serializable] public class TagGroup { public AssetGroupType groupType; public List<TagInfo> tags = new(); }
    [System.Serializable] private class SaveWrapper { public List<FavoriteGroup> groups = new(); public List<TagGroup> tagGroups = new(); }

    private Dictionary<AssetGroupType, FavoriteGroup> groupDict = new();
    private Dictionary<AssetGroupType, List<TagInfo>> tagDict = new();
    private Dictionary<string, Texture2D> thumbnailCache = new Dictionary<string, Texture2D>();
    private List<string> selectedTags = new();
    private string newTagName = string.Empty;
    private string searchKeyword = string.Empty;
    private Vector2 scrollPos;
    private AssetGroupType selectedGroup = AssetGroupType.Material;
    private string savePath => @"D:\00_PresetBackup\@Unity\@Editor_Json\_BookmarkData.json";
    private bool showTags = false;
    private bool autoSave = true;

    private string highlightGuid = null;
    private double highlightStartTime = 0;

    private string scrollToGuid = null;

    private Stack<SaveWrapper> undoStack = new();
    private Stack<SaveWrapper> redoStack = new();

    private string thumbSaveRoot => @"D:\00_PresetBackup\@Unity\@Editor_Json\_BookmarkData_Thumbnail";
    private Dictionary<string, string> customThumbnailMap = new();

    private double nextSaveTime = -1;


    [MenuItem("Tools/@FX_Tools/_Bookmark")]
    public static void ShowWindow()
    {
        var window = GetWindow<_Bookmark>();
        window.titleContent = new GUIContent("_Bookmark");
        window.Show();
    }

    private void OnEnable()
    {
        minSize = new Vector2(430, 420);
        LoadData();
        EnsureGroups();
        LoadCustomThumbnails();
        AssetPreview.SetPreviewTextureCacheSize(256);
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        EditorApplication.projectChanged -= ProjectChanged;
        EditorApplication.projectChanged += ProjectChanged;
        UndoRedoSubscribe(true);
        RestartIndex();
    }

    private void OnDisable()
    {
        EditorApplication.update -= Tick;
        EditorApplication.update -= DelayedSave;
        EditorApplication.projectChanged -= ProjectChanged;
        UndoRedoSubscribe(false);
        if (dataDirty && !loadFailed) SaveData();
        batchKind = BatchKind.None;
        batchQueue.Clear(); cleanupTargets.Clear(); autoTagResults.Clear();
        ClearPreviews();
    }

    private bool prefabCaptureUseScreenshot = false;
    private bool sceneCaptureUseScreenshot = false;

    private void LoadCustomThumbnails()
    {
        customThumbnailMap.Clear();
        foreach (var type in new[] { AssetGroupType.Prefab, AssetGroupType.Scene })
        foreach (var fav in groupDict[type].assets)
        customThumbnailMap[fav.guid] = Path.Combine(thumbSaveRoot, fav.guid + ".png");
    }

    private void CapturePrefabFromLatestScreenshot(string guid)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            if (!Directory.Exists(folder)) { EditorUtility.DisplayDialog("스크린샷", "스크린샷 폴더를 찾을 수 없습니다.", "확인"); return; }
            var latest = Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest == null) { EditorUtility.DisplayDialog("스크린샷", "스크린샷 이미지가 없습니다.", "확인"); return; }
            Texture2D original = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!original.LoadImage(File.ReadAllBytes(latest.FullName))) throw new InvalidDataException("이미지를 읽을 수 없습니다.");
                SaveCapturedTexture(guid, original);
            }
            finally { DestroyImmediate(original); }
        }
        catch (Exception ex) { Debug.LogError("[_Bookmark] 스크린샷 저장 실패: " + ex.Message); }
    }

    private void CapturePrefabToThumbnail(string prefabName, string guid) { CaptureViewToThumbnail(guid, false); }
    private void CaptureSceneWithUICamToThumbnail(string sceneName, string guid) { CaptureViewToThumbnail(guid, true); }

    private void CaptureViewToThumbnail(string guid, bool includeUI)
    {
        RenderTexture rt = null;
        Texture2D shot = null;
        GameObject temporaryCamera = null;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            Camera camera = null, uiCamera = null;
            var stage = includeUI ? PrefabStageUtility.GetCurrentPrefabStage() : null;
            if (stage != null)
            {
                camera = stage.scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Camera>(true)).FirstOrDefault();
                if (camera == null)
                {
                    temporaryCamera = new GameObject("TempPrefabCaptureCam") { hideFlags = HideFlags.HideAndDontSave };
                    camera = temporaryCamera.AddComponent<Camera>();
                    camera.backgroundColor = Color.gray; camera.clearFlags = CameraClearFlags.Color;
                    camera.orthographic = true; camera.orthographicSize = 1.5f;
                    camera.transform.position = new Vector3(0, 0, -10);
                }
            }
            else
            {
                var view = SceneView.lastActiveSceneView;
                if (view == null || view.camera == null) { EditorUtility.DisplayDialog("썸네일", "씬 뷰가 열려 있어야 합니다.", "확인"); return; }
                camera = view.camera;
                view.Repaint();
                if (includeUI) uiCamera = GameObject.FindObjectsOfType<Camera>(true).FirstOrDefault(c => c != camera && c.enabled && (c.GetComponent<Canvas>() != null || c.name.Contains("UI")));
            }
            rt = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.ARGB32);
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);
            RenderCamera(camera, rt);
            if (uiCamera != null) RenderCamera(uiCamera, rt);
            RenderTexture.active = rt;
            shot = new Texture2D(256, 256, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); shot.Apply();
            SaveCapturedTexture(guid, shot);
        }
        catch (Exception ex) { Debug.LogError("[_Bookmark] 썸네일 캡처 실패: " + ex.Message); }
        finally
        {
            RenderTexture.active = previousActive;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (shot != null) DestroyImmediate(shot);
            if (temporaryCamera != null) DestroyImmediate(temporaryCamera);
        }
    }

    private static void RenderCamera(Camera camera, RenderTexture target)
    {
        RenderTexture previous = camera.targetTexture;
        try { camera.targetTexture = target; camera.Render(); }
        finally { camera.targetTexture = previous; }
    }

    private void SaveCapturedTexture(string guid, Texture2D original)
    {
        Texture2D small = CopyPreview(original, 256);
        try
        {
            Directory.CreateDirectory(thumbSaveRoot);
            string file = Path.Combine(thumbSaveRoot, guid + ".png");
            File.WriteAllBytes(file, small.EncodeToPNG());
            customThumbnailMap[guid] = file;
            StoreThumbnail(guid, small);
            small = null; // Cache owns it now.
            Repaint();
        }
        finally { if (small != null) DestroyImmediate(small); }
    }

    private void RefreshThumbnail(string newPrefabName)
    {
        string file = Path.Combine(thumbSaveRoot, newPrefabName + "_Thumbnail.png");
        if (!File.Exists(file)) return;
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (texture.LoadImage(File.ReadAllBytes(file))) StoreThumbnail(newPrefabName, CopyPreview(texture, 256));
        }
        finally { DestroyImmediate(texture); }
        Repaint();
    }

    private void OnGUI()
    {
        bool guiEnabled = GUI.enabled;
        try
        {
            GUI.enabled = guiEnabled && batchKind == BatchKind.None;
            var nextGroup = (AssetGroupType)GUILayout.Toolbar((int)selectedGroup, System.Enum.GetNames(typeof(AssetGroupType)));
            if (nextGroup != selectedGroup)
            {
                selectedGroup = nextGroup;
                selectedTags.Clear();
                scrollPos = Vector2.zero;
                filteredDirty = true;
            }
            GUILayout.Space(10);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("에셋 이름 검색", GUILayout.Width(90));
                var newKeyword = EditorGUILayout.TextField(searchKeyword, GUILayout.ExpandWidth(true));
                if (newKeyword != searchKeyword)
                {
                    searchKeyword = newKeyword;
                    filteredDirty = true;
                    scrollPos = Vector2.zero;
                    Repaint();
                }
            }
            GUILayout.Space(5);

            DrawSettingsTags();
            GUILayout.Space(10);
            DrawShaderFilter();
            DrawTagFilter();
            GUILayout.Space(10);
            DrawAssetList();
            GUILayout.Space(10);
            DrawDragArea();
        }
        finally { GUI.enabled = guiEnabled; }

    }

    private void DrawTagFilter()
    {
        GUILayout.Label("태그 필터", EditorStyles.boldLabel);
        if (!tagDict.TryGetValue(selectedGroup, out var filterTags)) { GUILayout.Label("(태그 없음)"); return; }

        GUILayout.BeginVertical("box");

        float viewWidth = EditorGUIUtility.currentViewWidth - 40;
        float currentLineLength = 0f;
        float spacing = 5f;

        EditorGUILayout.BeginHorizontal();

        foreach (var tag in filterTags.OrderBy(t => t.name))
        {
            bool isSelected = selectedTags.Contains(tag.name);
            GUIContent content = new GUIContent(tag.name);
            Vector2 size = GUI.skin.button.CalcSize(content);

            if (currentLineLength + size.x + spacing > viewWidth && currentLineLength != 0)
            {
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                currentLineLength = 0f;
            }

            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = tag.color.ToColor();

            if (GUILayout.Button(content, GUILayout.Width(size.x)))
            {
                if (isSelected) selectedTags.Remove(tag.name);
                else selectedTags.Add(tag.name);
                filteredDirty = true;
                scrollPos = Vector2.zero;
            }
            GUI.backgroundColor = prev;

            if (isSelected) DrawOutline(GUILayoutUtility.GetLastRect());

            currentLineLength += size.x + spacing;
        }

        EditorGUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private void DrawSettingsTags()
    {
        GUIStyle boldFoldout = new GUIStyle(EditorStyles.foldout);
        boldFoldout.fontStyle = FontStyle.Bold;

        showTags = EditorGUILayout.Foldout(showTags, "태그 추가 및 관리", true, boldFoldout);
        if (!showTags) return;

        GUILayout.BeginHorizontal();

        GUI.SetNextControlName("NewTagField");

        newTagName = EditorGUILayout.TextField(newTagName);

        if (GUI.enabled && Event.current.isKey &&
        Event.current.keyCode == KeyCode.Return &&
        GUI.GetNameOfFocusedControl() == "NewTagField")
        {
            AddNewTag();
            Event.current.Use();
        }

        if (GUILayout.Button("Enter", GUILayout.Width(60)))
        {
            AddNewTag();
        }

        GUILayout.EndHorizontal();

        foreach (var tag in tagDict[selectedGroup].OrderBy(t => t.name).ToList())
        {
            GUILayout.BeginHorizontal();

            string prevName = tag.name;
            string updatedName = EditorGUILayout.TextField(prevName);

            if (updatedName != prevName && !string.IsNullOrEmpty(updatedName))
            {
                if (!tagDict[selectedGroup].Any(t => t != tag && t.name == updatedName))
                {
                    SaveStateToUndo();
                    tag.name = updatedName;
                    for (int n = 0; n < selectedTags.Count; n++)
                    if (selectedTags[n] == prevName) selectedTags[n] = updatedName;

                    foreach (var asset in groupDict[selectedGroup].assets)
                    {
                        for (int i = 0; i < asset.tags.Count; i++)
                        {
                            if (asset.tags[i] == prevName)
                            asset.tags[i] = updatedName;
                        }
                    }
                    Changed();
                }
            }

            Color newColor = EditorGUILayout.ColorField(tag.color.ToColor(), GUILayout.Width(60));
            if (newColor != tag.color.ToColor())
            {
                SaveStateToUndo();
                tag.color = new SerializableColor(newColor);
                Changed();
            }

            if (GUILayout.Button("X", GUILayout.Width(20)))
            {
                SaveStateToUndo();
                selectedTags.Remove(tag.name);
                tagDict[selectedGroup].Remove(tag);
                foreach (var asset in groupDict[selectedGroup].assets)
                asset.tags.RemoveAll(t => t == tag.name);

                Changed();
                SaveData();
                GUIUtility.ExitGUI();
            }

            GUILayout.EndHorizontal();
        }
    }

    private void AddNewTag()
    {
        string name = newTagName.Trim();
        if (name.Length > 0 && !tagDict[selectedGroup].Any(t =>
        string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SaveStateToUndo();
            tagDict[selectedGroup].Add(new TagInfo { name = name });
            Changed();
        }
        newTagName = string.Empty;
        GUI.FocusControl(null);
    }

    private void DrawDragArea()
    {
        Rect dropArea = GUILayoutUtility.GetRect(0, 100, GUILayout.ExpandWidth(true));

        GUIStyle dropTextStyle = new GUIStyle(GUI.skin.box);
        dropTextStyle.fontSize = 16;
        dropTextStyle.alignment = TextAnchor.MiddleCenter;

        GUI.Box(dropArea, "여기로 드래그하여 추가", dropTextStyle);

        HandleDrag(dropArea);
    }

    private void DrawAssetList()
    {
        var list = groupDict[selectedGroup].assets;
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(selectedGroup + " 그룹", GUILayout.Width(95));
            if (selectedGroup == AssetGroupType.Prefab)
            prefabCaptureUseScreenshot = GUILayout.Toggle(prefabCaptureUseScreenshot, "Win + Shift + S");
            else if (selectedGroup == AssetGroupType.Scene)
            sceneCaptureUseScreenshot = GUILayout.Toggle(sceneCaptureUseScreenshot, "Win + Shift + S");
            GUILayout.FlexibleSpace();
            if (indexQueue.Count > 0) GUILayout.Label("목록 확인 중: " + indexQueue.Count, EditorStyles.miniLabel);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("☰Json", GUILayout.Width(58)))
            EditorUtility.RevealInFinder(File.Exists(savePath) ? savePath : Path.GetDirectoryName(savePath));
            if (GUILayout.Button(new GUIContent("JSON 정리", "현재 프로젝트에 존재하지 않는 북마크를 모든 그룹에서 제거합니다. 태그 정의는 유지합니다."))) StartCleanup();
            if (GUILayout.Button(new GUIContent("자동 태그", "저장된 모든 머터리얼의 이름 및 연결된 모든 텍스처 이름으로 기존 태그를 추가합니다."))) StartAutoTags();
            if (GUILayout.Button("↶", GUILayout.Width(27))) Undo();
            if (GUILayout.Button("↷", GUILayout.Width(27))) Redo();
            if (GUILayout.Button("설정 저장", GUILayout.Width(70)))
            {
                if (SaveData()) ShowNotification(new GUIContent("설정 저장 완료"));
            }
        }
        if (loadFailed) EditorGUILayout.HelpBox("JSON 읽기 실패: 원본 보호를 위해 저장이 중지되었습니다. Console에서 원인을 확인하고 파일을 복구한 뒤 창을 다시 열어 주세요.", MessageType.Error);
        if (!string.IsNullOrEmpty(saveError)) EditorGUILayout.HelpBox(saveError, MessageType.Error);
        if (batchKind != BatchKind.None)
        EditorGUILayout.HelpBox(batchKind == BatchKind.Cleanup ? "현재 프로젝트 기준으로 JSON 정리 중…" : "기존 머터리얼 자동 태그 연결 중…", MessageType.Info);

        if (Event.current.type == EventType.Layout) RebuildFiltered();
        Rect viewport = GUILayoutUtility.GetRect(0, 100000, 100, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        // GUILayout returns a placeholder rectangle during Layout. Keep the last real viewport
        // so layout passes do not rebuild row measurements or disturb the scroll position.
        if (Event.current.type == EventType.Layout) viewport = lastGridRect;
        else lastGridRect = viewport;
        float cardWidth = Mathf.Max(180, (position.width - 24) / 2);
        if (rowStarts.Count == 0 || Mathf.Abs(cardWidth - lastCardWidth) > 1)
        BuildRows(cardWidth);
        if (!string.IsNullOrEmpty(scrollToGuid))
        {
            int at = filteredAssets.FindIndex(a => a.guid == scrollToGuid);
            if (at >= 0) scrollPos.y = rowStarts[at / 2];
            if (at >= 0 || indexQueue.Count == 0) scrollToGuid = null;
        }
        scrollPos = GUI.BeginScrollView(viewport, scrollPos, new Rect(0, 0, viewport.width - 18, contentHeight));
        visiblePreviewGuids.Clear();
        // Binary search skips off-screen rows; GUI work scales with visible cards.
        int firstRow = rowStarts.BinarySearch(scrollPos.y);
        if (firstRow < 0) firstRow = Mathf.Max(0, ~firstRow - 1);
        for (int row = firstRow; row < rowStarts.Count && rowStarts[row] < scrollPos.y + viewport.height + 160; row++)
        {
            float cardHeight = rowHeights[row] - CardRowGap;
            for (int col = 0; col < 2; col++)
            {
                int idx = row * 2 + col;
                if (idx >= filteredAssets.Count) break;
                var fav = filteredAssets[idx];
                if (!assetInfo.TryGetValue(fav.guid, out var info) || info.asset == null) continue;
                var obj = info.asset;
                Rect cardRect = new Rect(col * cardWidth, rowStarts[row], cardWidth - 4, cardHeight);
                GUI.BeginGroup(cardRect, GUI.skin.box);
                DrawAssetCard(list, fav, obj, cardWidth - 4, cardHeight);
                GUI.EndGroup();
            }
        }
        GUI.EndScrollView();
    }

    private void DrawAssetCard(List<FavoriteAsset> list, FavoriteAsset fav, Object obj, float cardWidth, float cardHeight)
    {
        Rect dragRect = new Rect(4, 5, 16, 64);
        GUI.Label(dragRect, "≡", EditorStyles.centeredGreyMiniLabel);
        Event evt = Event.current;
        if (GUI.enabled && evt.type == EventType.MouseDown && dragRect.Contains(evt.mousePosition))
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new Object[0];
            DragAndDrop.SetGenericData("DraggedItem", fav);
            DragAndDrop.StartDrag("Drag");
            evt.Use();
        }
        if (GUI.enabled && evt.type == EventType.DragUpdated && dragRect.Contains(evt.mousePosition))
        { DragAndDrop.visualMode = DragAndDropVisualMode.Move; evt.Use(); }
        if (GUI.enabled && evt.type == EventType.DragPerform && dragRect.Contains(evt.mousePosition))
        {
            var dragged = DragAndDrop.GetGenericData("DraggedItem") as FavoriteAsset;
            if (dragged != null && dragged != fav && list.Contains(dragged))
            {
                SaveStateToUndo();
                list.Remove(dragged);
                int insertAt = list.IndexOf(fav);
                list.Insert(insertAt < 0 ? list.Count : insertAt, dragged);
                Changed();
            }
            DragAndDrop.SetGenericData("DraggedItem", null);
            DragAndDrop.AcceptDrag(); evt.Use();
        }
        Texture2D texture = GetThumbnail(fav.guid, obj);
        Rect previewRect = new Rect(23, 5, 64, 64);
        if (texture != null) GUI.DrawTexture(previewRect, texture, ScaleMode.ScaleToFit);
        if (GUI.enabled && evt.type == EventType.MouseDown && previewRect.Contains(evt.mousePosition))
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData("DraggedItem", null);
            DragAndDrop.objectReferences = new Object[] { obj };
            DragAndDrop.StartDrag("Dragging " + obj.name); evt.Use();
        }
        string shaderName = assetInfo.TryGetValue(fav.guid, out var info) ? info.shader : "";
        if (shaderName.IndexOf("ui", StringComparison.OrdinalIgnoreCase) >= 0)
        DrawOutline(previewRect, new Color(1, 1, 0, 0.7f));
        if (fav.tags.Contains(RedXTagName))
        {
            Handles.BeginGUI();
            Color old = Handles.color; Handles.color = Color.red;
            Handles.DrawAAPolyLine(2, new Vector3(previewRect.xMin, previewRect.yMin), new Vector3(previewRect.xMax, previewRect.yMax));
            Handles.DrawAAPolyLine(2, new Vector3(previewRect.xMax, previewRect.yMin), new Vector3(previewRect.xMin, previewRect.yMax));
            Handles.color = old; Handles.EndGUI();
        }
        if (fav.guid == highlightGuid && EditorApplication.timeSinceStartup - highlightStartTime < 1.5)
        {
            float t = (float)(EditorApplication.timeSinceStartup - highlightStartTime);
            DrawOutline(previewRect, Color.Lerp(Color.white, Color.cyan, Mathf.Sin(t * Mathf.PI * 2) * 0.5f + 0.5f));
            Repaint();
        }
        float left = 96, width = Mathf.Max(35, cardWidth - left - 5), top = 5;
        EditorGUI.ObjectField(new Rect(left, top, Mathf.Max(10, width - 24), 20), obj, typeof(Object), false);
        if (GUI.Button(new Rect(cardWidth - 25, top, 20, 20), "X"))
        {
            SaveStateToUndo(); list.Remove(fav); Changed(); GUIUtility.ExitGUI();
        }
        top += 24;
        if (shaderName.Length > 0)
        {
            var style = new GUIStyle(EditorStyles.label) { fontSize = 10, wordWrap = true };
            style.normal.textColor = shaderName.IndexOf("flowdistortion", StringComparison.OrdinalIgnoreCase) >= 0 ? new Color(0.88f, 0.52f, 1) :
            shaderName.IndexOf("additive", StringComparison.OrdinalIgnoreCase) >= 0 ? Color.yellow :
            shaderName.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0 ? Color.cyan : new Color(0.75f, 0.75f, 0.75f);
            float height = style.CalcHeight(new GUIContent(shaderName), width);
            GUI.Label(new Rect(left, top, width, height), new GUIContent(shaderName, shaderName), style);
            top += height + 4;
        }
        Rect arrow = new Rect(left, top, 20, 18);
        if (EditorGUI.DropdownButton(arrow, new GUIContent("▾"), FocusType.Passive, EditorStyles.popup))
        {
            var tags = tagDict[selectedGroup].OrderBy(t => t.name).ToList();
            PopupWindow.Show(arrow, new TagPopupPicker(this, fav, tags, Mathf.Min(400, tags.Count * 24 + 10)));
        }
        float x = 24, y = top;
        foreach (string tagName in fav.tags.OrderBy(n => n))
        {
            var tag = tagDict[selectedGroup].FirstOrDefault(t => t.name == tagName);
            if (tag == null) continue;
            float w = Mathf.Min(width, GUI.skin.box.CalcSize(new GUIContent(tagName)).x);
            if (x + w > width) { x = 0; y += 22; }
            Color prev = GUI.backgroundColor; GUI.backgroundColor = tag.color.ToColor();
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 10 };
            style.normal.textColor = Color.white;
            GUI.Box(new Rect(left + x, y, w, 18), new GUIContent(tagName, tagName), style);
            GUI.backgroundColor = prev;
            x += w + 4;
        }
        if (selectedGroup == AssetGroupType.Prefab || selectedGroup == AssetGroupType.Scene)
        {
            if (GUI.Button(new Rect(cardWidth - 30, cardHeight - 20, 26, 16), "⦿"))
            {
                if ((selectedGroup == AssetGroupType.Prefab && prefabCaptureUseScreenshot) || (selectedGroup == AssetGroupType.Scene && sceneCaptureUseScreenshot))
                CapturePrefabFromLatestScreenshot(fav.guid);
                else if (selectedGroup == AssetGroupType.Prefab) CapturePrefabToThumbnail(obj.name, fav.guid);
                else CaptureSceneWithUICamToThumbnail(obj.name, fav.guid);
                LoadCustomThumbnails(); Repaint();
            }
        }
    }

    private class TagPopupPicker : PopupWindowContent
    {
        private readonly _Bookmark owner;
        private readonly FavoriteAsset fav;
        private readonly List<TagInfo> tags;
        private readonly float height;
        private Vector2 scroll;
        public TagPopupPicker(_Bookmark owner, FavoriteAsset fav, List<TagInfo> tags, float height)
        { this.owner = owner; this.fav = fav; this.tags = tags; this.height = Mathf.Clamp(height, 40, 400); }
        public override Vector2 GetWindowSize() => new Vector2(220, height);
        public override void OnGUI(Rect rect)
        {
            if (owner == null) { editorWindow.Close(); return; }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var tag in tags)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var prev = GUI.backgroundColor;
                    GUI.backgroundColor = tag.color.ToColor();
                    GUILayout.Box("", GUILayout.Width(12), GUILayout.Height(12));
                    GUI.backgroundColor = prev;
                    bool has = fav.tags.Contains(tag.name);
                    bool sel = EditorGUILayout.ToggleLeft(tag.name, has);
                    if (sel != has)
                    {
                        owner.SaveStateToUndo();
                        if (sel) fav.tags.Add(tag.name); else fav.tags.Remove(tag.name);
                        owner.Changed();
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawOutline(Rect rect, Color? color = null)
    {
        Handles.BeginGUI();
        Handles.color = color ?? Color.white;
        Handles.DrawSolidRectangleWithOutline(rect, Color.clear, Handles.color);
        Handles.EndGUI();
    }

    private void HandleDrag(Rect area)
    {
        Event evt = Event.current;
        if (!GUI.enabled || !area.Contains(evt.mousePosition) || (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;
        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            SaveStateToUndo();
            var known = groupDict.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value.assets.Select(a => a.guid)));
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj == null) continue;
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;
                selectedGroup = DetectAssetGroup(obj);
                selectedTags.Clear();
                if (!known[selectedGroup].Add(guid))
                {
                    highlightGuid = guid;
                    highlightStartTime = EditorApplication.timeSinceStartup;
                    scrollToGuid = guid;
                    continue;
                }
                var fav = new FavoriteAsset { guid = guid };
                if (obj is Material material) AddMatchingTags(fav, material);
                groupDict[selectedGroup].assets.Add(fav);
                indexQueue.Enqueue(guid);
                scrollToGuid = guid;
            }
            LoadCustomThumbnails();
            Changed();
        }
        evt.Use();
    }

    private void DelayedSave()
    {
        if (dataDirty && nextSaveTime > 0 && EditorApplication.timeSinceStartup >= nextSaveTime && batchKind == BatchKind.None)
        SaveData();
    }

    private AssetGroupType DetectAssetGroup(Object obj)
    {
        if (obj is Material) return AssetGroupType.Material;
        if (obj is Texture || obj is Texture2D || obj is Sprite) return AssetGroupType.Texture;
        if (obj is Mesh) return AssetGroupType.Mesh;
        if (obj is GameObject)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (PrefabUtility.GetPrefabAssetType(obj) != PrefabAssetType.NotAPrefab)
            return AssetGroupType.Prefab;
        }
        if (obj is SceneAsset) return AssetGroupType.Scene;
        if (obj is Shader) return AssetGroupType.Shader;


        return selectedGroup;
    }

    private void SaveStateToUndo()
    {
        var wrapper = new SaveWrapper
        {
            groups = groupDict.Values.Select(g => new FavoriteGroup
            {
                groupType = g.groupType,
                assets = g.assets.Select(a => new FavoriteAsset { guid = a.guid, tags = new List<string>(a.tags) }).ToList()
            }).ToList(),
            tagGroups = tagDict.Select(kvp => new TagGroup
            {
                groupType = kvp.Key,
                tags = kvp.Value.Select(t => new TagInfo { name = t.name, color = new SerializableColor(t.color.ToColor()) }).ToList()
            }).ToList()
        };

        undoStack.Push(wrapper);
        TrimUndoHistory();
        redoStack.Clear();
    }

    private void Undo()
    {
        if (undoStack.Count == 0) return;
        SaveWrapper current = GetCurrentState();
        redoStack.Push(current);
        ApplyState(undoStack.Pop());
        Changed();
        SaveData();
    }

    private void Redo()
    {
        if (redoStack.Count == 0) return;
        SaveWrapper current = GetCurrentState();
        undoStack.Push(current);
        TrimUndoHistory();
        ApplyState(redoStack.Pop());
        Changed();
        SaveData();
    }

    private SaveWrapper GetCurrentState()
    {
        return new SaveWrapper
        {
            groups = groupDict.Values.Select(g => new FavoriteGroup
            {
                groupType = g.groupType,
                assets = g.assets.Select(a => new FavoriteAsset { guid = a.guid, tags = new List<string>(a.tags) }).ToList()
            }).ToList(),
            tagGroups = tagDict.Select(kvp => new TagGroup
            {
                groupType = kvp.Key,
                tags = kvp.Value.Select(t => new TagInfo { name = t.name, color = new SerializableColor(t.color.ToColor()) }).ToList()
            }).ToList()
        };
    }

    private void ApplyState(SaveWrapper state)
    {
        groupDict = state.groups.ToDictionary(g => g.groupType, g => g);
        tagDict = state.tagGroups.ToDictionary(kvp => kvp.groupType, kvp => kvp.tags);
        EnsureGroups();
        selectedTags.RemoveAll(t => !tagDict[selectedGroup].Any(x => x.name == t));
        LoadCustomThumbnails();
        RestartIndex();
    }

    private bool SaveData()
    {
        if (loadFailed) { saveError = "JSON 읽기에 실패하여 원본 보호를 위해 저장을 중지했습니다."; return false; }
        string temp = savePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var wrapper = new SaveWrapper { groups = groupDict.Values.ToList(), tagGroups = tagDict.Select(k => new TagGroup { groupType = k.Key, tags = k.Value }).ToList() };
            string json = JsonUtility.ToJson(wrapper, false);
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            // A complete temporary file replaces the destination atomically. Last good version stays in .bak.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(savePath)) File.Replace(temp, savePath, savePath + ".bak");
            else File.Move(temp, savePath);
            dataDirty = false;
            nextSaveTime = -1;
            saveError = null;
            filteredDirty = true;
            Repaint();
            return true;
        }
        catch (Exception ex)
        {
            dataDirty = true;
            saveError = "JSON 저장 실패: " + ex.Message;
            nextSaveTime = -1; // Retry on the next edit or manual save, not every editor frame.
            Debug.LogError("[_Bookmark] " + saveError);
            return false;
        }
        finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch { } } }
    }

    private void LoadData()
    {
        if (!File.Exists(savePath)) { EnsureGroups(); return; }
        try
        {
            var wrapper = JsonUtility.FromJson<SaveWrapper>(File.ReadAllText(savePath));
            if (wrapper == null || wrapper.groups == null || wrapper.tagGroups == null)
            throw new InvalidDataException("JSON groups/tagGroups가 올바르지 않습니다.");
            groupDict.Clear(); tagDict.Clear();
            foreach (var group in wrapper.groups)
            {
                if (group == null) continue;
                if (!groupDict.TryGetValue(group.groupType, out var target))
                groupDict[group.groupType] = target = new FavoriteGroup { groupType = group.groupType };
                var known = new HashSet<string>(target.assets.Select(x => x.guid));
                foreach (var fav in group.assets ?? new List<FavoriteAsset>())
                {
                    if (fav == null || string.IsNullOrEmpty(fav.guid) || !known.Add(fav.guid)) continue;
                    fav.tags = (fav.tags ?? new List<string>()).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
                    target.assets.Add(fav);
                }
            }
            foreach (var group in wrapper.tagGroups)
            {
                if (group == null) continue;
                if (!tagDict.TryGetValue(group.groupType, out var tags)) tagDict[group.groupType] = tags = new List<TagInfo>();
                foreach (var tag in group.tags ?? new List<TagInfo>())
                {
                    if (tag == null || string.IsNullOrEmpty(tag.name) || tags.Any(t => t.name == tag.name)) continue;
                    if (tag.color == null) tag.color = new SerializableColor(Color.gray);
                    tags.Add(tag);
                }
            }
            EnsureGroups();
        }
        catch (Exception ex)
        {
            loadFailed = true;
            Debug.LogError("[_Bookmark] JSON 읽기 실패. 원본을 보호합니다: " + ex.Message);
            EnsureGroups();
        }
    }

    public static List<FavoriteGroup> GetCurrentFavorites(out Dictionary<AssetGroupType, List<TagInfo>> tags)
    {
        var tool = CreateInstance<_Bookmark>();
        try
        {
            // OnEnable already loads the data; do not load it twice.
            tags = tool.tagDict;
            return tool.groupDict.Values.ToList();
        }
        finally { DestroyImmediate(tool); }
    }

    // v1.4 cached indexing, virtual grid, preview scheduling, and incremental bulk operations.
    private sealed class AssetInfo
    {
        public Object asset;
        public string name;
        public string shader;
        public string root; // Cached configured category, not the first path segment.
        public string shaderLeaf;
        public int dirtyCount;
        public Hash128 dependencyHash;
    }
    private readonly Dictionary<string, AssetInfo> assetInfo = new Dictionary<string, AssetInfo>();
    private readonly Queue<string> indexQueue = new Queue<string>();
    private readonly List<FavoriteAsset> filteredAssets = new List<FavoriteAsset>();
    private readonly List<float> rowStarts = new List<float>();
    private readonly List<float> rowHeights = new List<float>();
    private readonly HashSet<string> visiblePreviewGuids = new HashSet<string>();
    private readonly Dictionary<string, double> previewTouched = new Dictionary<string, double>();
    private readonly Dictionary<string, double> previewRequested = new Dictionary<string, double>();
    private readonly Dictionary<string, Texture2D> miniThumbnails = new Dictionary<string, Texture2D>();
    private readonly HashSet<string> missingCustomThumbnails = new HashSet<string>();
    private readonly Dictionary<string, string> shaderCategoryCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> shaderNameTooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private List<string> shaderRoots = new List<string>();
    private List<string> shaderNames = new List<string>();
    private string selectedShaderRoot = "";
    private string selectedShader = "";
    private bool filteredDirty = true, shaderOptionsDirty = true, dataDirty, loadFailed;
    private float lastCardWidth = -1, contentHeight;
    private double reindexAt = -1, nextPreviewPoll, nextIndexRepaint;
    private string saveError;
    private int previewCursor;
    private double lastFocusRefresh;
    private Vector2 shaderFilterScroll;
    private Rect lastGridRect = new Rect(0, 0, 400, 200);
    private enum BatchKind { None, Cleanup, AutoTags }
    private BatchKind batchKind;
    private readonly Queue<FavoriteAsset> batchQueue = new Queue<FavoriteAsset>();
    private readonly HashSet<FavoriteAsset> cleanupTargets = new HashSet<FavoriteAsset>();
    private readonly Dictionary<FavoriteAsset, List<string>> autoTagResults = new Dictionary<FavoriteAsset, List<string>>();
    private string cleanupBackup;

    private void EnsureGroups()
    {
        foreach (AssetGroupType type in Enum.GetValues(typeof(AssetGroupType)))
        {
            if (!groupDict.ContainsKey(type)) groupDict[type] = new FavoriteGroup { groupType = type };
            if (!tagDict.ContainsKey(type)) tagDict[type] = new List<TagInfo>();
        }
    }

    private void Changed()
    {
        dataDirty = true;
        filteredDirty = true;
        shaderOptionsDirty = true;
        if (autoSave) nextSaveTime = EditorApplication.timeSinceStartup + 0.5;
        Repaint();
    }

    private void UndoRedoSubscribe(bool add)
    {
        UnityEditor.Undo.undoRedoPerformed -= ProjectChanged;
        if (add) UnityEditor.Undo.undoRedoPerformed += ProjectChanged;
    }

    private void ProjectChanged()
    {
        // Imports and inspector edits may arrive in bursts. Rebuild once after they settle.
        reindexAt = EditorApplication.timeSinceStartup + 0.5;
        if (batchKind != BatchKind.None)
        {
            batchKind = BatchKind.None;
            batchQueue.Clear(); cleanupTargets.Clear(); autoTagResults.Clear();
            ShowNotification(new GUIContent("프로젝트가 변경되어 일괄 처리를 취소했습니다. 다시 실행해 주세요."));
        }
    }

    private void OnFocus()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now - lastFocusRefresh < 2 || indexQueue.Count > 0 || reindexAt > 0) return;
        lastFocusRefresh = now;
        reindexAt = now + 0.5;
    }

    private void RestartIndex()
    {
        assetInfo.Clear(); indexQueue.Clear();
        shaderCategoryCache.Clear();
        foreach (string guid in groupDict.Values.SelectMany(g => g.assets).Select(a => a.guid).Distinct())
        indexQueue.Enqueue(guid);
        ClearPreviews();
        filteredDirty = shaderOptionsDirty = true;
        rowStarts.Clear();
        Repaint();
    }

    private void IndexOne(string guid)
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        Object obj = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Object>(path);
        var info = new AssetInfo { asset = obj, name = obj == null ? "" : obj.name, shader = "", root = "", shaderLeaf = "" };
        if (obj is Material mat && mat.shader != null)
        {
            info.shader = mat.shader.name;
            info.root = ClassifyShader(info.shader);
            info.shaderLeaf = info.shader.Substring(info.shader.LastIndexOf('/') + 1);
        }
        info.dirtyCount = obj == null ? 0 : EditorUtility.GetDirtyCount(obj);
        info.dependencyHash = string.IsNullOrEmpty(path) ? default(Hash128) : AssetDatabase.GetAssetDependencyHash(path);
        if (assetInfo.TryGetValue(guid, out var old) && (old.asset != info.asset || old.shader != info.shader || old.name != info.name || old.dirtyCount != info.dirtyCount || old.dependencyHash != info.dependencyHash))
        {
            if (thumbnailCache.TryGetValue(guid, out var thumb) && thumb != null) DestroyImmediate(thumb);
            thumbnailCache.Remove(guid); miniThumbnails.Remove(guid); previewRequested.Remove(guid);
        }
        assetInfo[guid] = info;
        filteredDirty = shaderOptionsDirty = true;
    }

    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (reindexAt > 0 && now >= reindexAt)
        {
            reindexAt = -1;
            indexQueue.Clear();
            foreach (string guid in groupDict.Values.SelectMany(g => g.assets).Select(a => a.guid).Distinct()) indexQueue.Enqueue(guid);
        }
        if (batchKind != BatchKind.None) TickBatch();
        double until = EditorApplication.timeSinceStartup + 0.004;
        int count = 0;
        while (indexQueue.Count > 0 && count++ < 24 && EditorApplication.timeSinceStartup < until)
        {
            string guid = indexQueue.Dequeue();
            try { IndexOne(guid); }
            catch (Exception ex) { Debug.LogWarning("[_Bookmark] 에셋 확인 실패 " + guid + ": " + ex.Message); }
        }
        if (count > 0 && (now >= nextIndexRepaint || indexQueue.Count == 0))
        {
            nextIndexRepaint = now + 0.15;
            Repaint();
        }
        if (now >= nextPreviewPoll)
        {
            nextPreviewPoll = now + 0.15;
            PollVisiblePreviews();
        }
        DelayedSave();
    }

    private void RebuildFiltered()
    {
        if (!filteredDirty) return;
        filteredDirty = false;
        filteredAssets.Clear();
        foreach (var fav in groupDict[selectedGroup].assets)
        {
            if (!assetInfo.TryGetValue(fav.guid, out var info) || info.asset == null) continue;
            if (!string.IsNullOrEmpty(searchKeyword) && info.name.IndexOf(searchKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
            // Keep the original OR behavior between selected tags.
            if (selectedTags.Count > 0 && !fav.tags.Any(t => selectedTags.Contains(t))) continue;
            if (selectedGroup == AssetGroupType.Material)
            {
                if (selectedShaderRoot.Length > 0 && info.root != selectedShaderRoot) continue;
                if (selectedShader.Length > 0 && !string.Equals(info.shaderLeaf, selectedShader, StringComparison.OrdinalIgnoreCase)) continue;
            }
            filteredAssets.Add(fav);
        }
        rowStarts.Clear();
    }

    private const float CardRowGap = 4f;

    private void BuildRows(float cardWidth)
    {
        lastCardWidth = cardWidth;
        rowStarts.Clear(); rowHeights.Clear();
        contentHeight = 0;
        for (int idx = 0; idx < filteredAssets.Count; idx += 2)
        {
            float height = MeasureCard(filteredAssets[idx], cardWidth - 4);
            if (idx + 1 < filteredAssets.Count) height = Mathf.Max(height, MeasureCard(filteredAssets[idx + 1], cardWidth - 4));
            rowStarts.Add(contentHeight);
            rowHeights.Add(height + CardRowGap);
            contentHeight += height + CardRowGap;
        }
        contentHeight = Mathf.Max(1, contentHeight);
    }

    private float MeasureCard(FavoriteAsset fav, float cardWidth)
    {
        float width = Mathf.Max(35, cardWidth - 101);
        float x = 24, y = 0;
        foreach (string tag in fav.tags.OrderBy(t => t))
        {
            if (!tagDict[selectedGroup].Any(t => t.name == tag)) continue;
            float w = Mathf.Min(width, GUI.skin.box.CalcSize(new GUIContent(tag)).x);
            if (x + w > width) { x = 0; y += 22; }
            x += w + 4;
        }
        float shaderHeight = 0;
        if (assetInfo.TryGetValue(fav.guid, out var info) && info.shader.Length > 0)
        {
            var style = new GUIStyle(EditorStyles.label) { fontSize = 10, wordWrap = true };
            shaderHeight = style.CalcHeight(new GUIContent(info.shader), width);
        }
        // Match the actual bottom of the preview/tags instead of reserving a large
        // empty footer on every card. Extra room is needed only for capture controls.
        float contentBottom = 29 + (shaderHeight > 0 ? shaderHeight + 4 : 0) + y + 18;
        bool hasCaptureButton = selectedGroup == AssetGroupType.Prefab || selectedGroup == AssetGroupType.Scene;
        float height = Mathf.Max(69, contentBottom) + 4;
        return hasCaptureButton ? Mathf.Max(93, height + 20) : height;
    }

    private string ClassifyShader(string shaderName)
    {
        if (string.IsNullOrEmpty(shaderName)) return "";
        if (shaderCategoryCache.TryGetValue(shaderName, out string cached)) return cached;
        string[] segments = shaderName.Split('/');
        string fallback = "";
        foreach (var rule in ShaderCategories)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Name)) continue;
            if (rule.CatchAll)
            {
                if (fallback.Length == 0) fallback = rule.Name;
                continue;
            }
            bool matchesSegment = rule.PathSegments.Any(segment => !string.IsNullOrWhiteSpace(segment) &&
            segments.Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase)));
            bool matchesPrefix = rule.Prefixes.Any(prefix => !string.IsNullOrWhiteSpace(prefix) &&
            shaderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (matchesSegment || matchesPrefix)
            {
                shaderCategoryCache[shaderName] = rule.Name;
                return rule.Name;
            }
        }
        shaderCategoryCache[shaderName] = fallback;
        return fallback;
    }

    private void RebuildShaderOptions()
    {
        if (!shaderOptionsDirty) return;
        shaderOptionsDirty = false;
        // Keep configured categories visible and in their configured order, including empty categories.
        shaderRoots = ShaderCategories.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
        .Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selectedShaderRoot.Length > 0 && !shaderRoots.Contains(selectedShaderRoot))
        {
            selectedShaderRoot = "";
            selectedShader = "";
            filteredDirty = true;
        }
        var infos = groupDict[AssetGroupType.Material].assets
        .Where(a => assetInfo.ContainsKey(a.guid)).Select(a => assetInfo[a.guid])
        .Where(i => i.asset != null && i.shader.Length > 0 &&
        (selectedShaderRoot.Length == 0 || i.root == selectedShaderRoot)).ToList();
        // One button per final shader name. Full paths are retained in the tooltip and asset metadata.
        var leafGroups = infos.GroupBy(i => i.shaderLeaf, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        shaderNames = leafGroups.Select(g => g.Key).ToList();
        shaderNameTooltips.Clear();
        foreach (var group in leafGroups)
        shaderNameTooltips[group.Key] = string.Join("\n", group.Select(i => i.shader)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        // During incremental indexing, keep a selected leaf until its assets have been reached.
        if (indexQueue.Count == 0 && selectedShader.Length > 0 && !shaderNames.Contains(selectedShader, StringComparer.OrdinalIgnoreCase))
        {
            selectedShader = "";
            filteredDirty = true;
        }
    }

    private void DrawShaderFilter()
    {
        if (selectedGroup != AssetGroupType.Material) return;
        if (Event.current.type == EventType.Layout) RebuildShaderOptions();
        GUILayout.BeginVertical("box");
        // A MaxHeight alone lets the asset grid take nearly all available height.
        // Reserve the measured button rows explicitly so this region cannot collapse.
        float filterHeight = Mathf.Clamp(MeasureShaderButtons(shaderRoots, true) + MeasureShaderButtons(shaderNames, false) + 8f, 100f, 200f);
        shaderFilterScroll = EditorGUILayout.BeginScrollView(shaderFilterScroll, false, false,
        GUILayout.Height(filterHeight), GUILayout.ExpandHeight(false));
        DrawShaderButtons("분류", shaderRoots, true);
        DrawShaderButtons("셰이더", shaderNames, false);
        EditorGUILayout.EndScrollView();
        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    private void DrawShaderButtons(string label, List<string> values, bool root)
    {
        GUILayout.Label(label, EditorStyles.miniBoldLabel, GUILayout.Height(18));
        float used = 0, available = ShaderFilterAvailableWidth();
        GUILayout.BeginHorizontal();
        var all = new List<string> { "" };
        all.AddRange(values);
        foreach (string value in all)
        {
            GUIContent content = ShaderFilterContent(value, values, root);
            float w = Mathf.Min(available, GUI.skin.button.CalcSize(content).x + 8);
            if (used + w > available && used > 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); used = 0; }
            bool active = (root ? selectedShaderRoot : selectedShader) == value;
            bool clicked = GUILayout.Toggle(active, content, GUI.skin.button, GUILayout.Width(w), GUILayout.Height(22));
            if (clicked != active)
            {
                if (root) { selectedShaderRoot = clicked ? value : ""; selectedShader = ""; shaderOptionsDirty = true; }
                else selectedShader = clicked ? value : "";
                filteredDirty = true; scrollPos = Vector2.zero;
            }
            used += w + 4;
        }
        GUILayout.EndHorizontal();
    }

    private float ShaderFilterAvailableWidth()
    {
        // Account for box padding and the vertical scrollbar, even before it appears.
        return Mathf.Max(100f, position.width - 60f);
    }

    private GUIContent ShaderFilterContent(string value, List<string> values, bool root)
    {
        if (value.Length == 0) return new GUIContent("전체", root ? "모든 분류" : "선택한 분류의 모든 셰이더");
        string tooltip = !root && shaderNameTooltips.TryGetValue(value, out var paths) ? paths : value;
        return new GUIContent(value, tooltip);
    }

    private float MeasureShaderButtons(List<string> values, bool root)
    {
        float available = ShaderFilterAvailableWidth(), used = 0f;
        int rows = 1;
        var all = new List<string> { "" };
        all.AddRange(values);
        foreach (string value in all)
        {
            float width = Mathf.Min(available, GUI.skin.button.CalcSize(ShaderFilterContent(value, values, root)).x + 8f);
            if (used + width > available && used > 0f) { rows++; used = 0f; }
            used += width + 4f;
        }
        // Caption + 22-point buttons with normal IMGUI vertical margins.
        return 22f + rows * 26f;
    }

    private bool IsUITexture(Material mat, out Texture2D texture)
    {
        texture = null;
        if (mat == null || mat.shader == null || mat.shader.name.IndexOf("ui", StringComparison.OrdinalIgnoreCase) < 0) return false;
        texture = mat.mainTexture as Texture2D;
        return texture != null;
    }

    private Texture2D GetThumbnail(string guid, Object obj)
    {
        visiblePreviewGuids.Add(guid);
        previewTouched[guid] = EditorApplication.timeSinceStartup;
        if (thumbnailCache.TryGetValue(guid, out var cached) && cached != null) return cached;
        if (customThumbnailMap.TryGetValue(guid, out string file) && !missingCustomThumbnails.Contains(guid))
        {
            if (File.Exists(file))
            {
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (tex.LoadImage(File.ReadAllBytes(file)))
                    {
                        // Limit screenshots as well as generated previews to a bounded size.
                        Texture2D small = CopyPreview(tex, 256);
                        DestroyImmediate(tex);
                        StoreThumbnail(guid, small);
                        return small;
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[_Bookmark] 썸네일 읽기 실패: " + ex.Message); }
                if (tex != null) DestroyImmediate(tex);
            }
            missingCustomThumbnails.Add(guid);
        }
        if (obj is Material mat && IsUITexture(mat, out var main)) return main;
        if (!miniThumbnails.TryGetValue(guid, out var mini) || mini == null)
        {
            mini = AssetPreview.GetMiniThumbnail(obj);
            miniThumbnails[guid] = mini; // Unity owns mini thumbnails; never destroy them.
        }
        return mini;
    }

    private void PollVisiblePreviews()
    {
        int requests = 0;
        double now = EditorApplication.timeSinceStartup;
        var visible = visiblePreviewGuids.ToArray();
        for (int i = 0; i < visible.Length; i++)
        {
            string guid = visible[(previewCursor + i) % visible.Length];
            if (thumbnailCache.TryGetValue(guid, out var cached) && cached != null) continue;
            if (!assetInfo.TryGetValue(guid, out var info) || info.asset == null) continue;
            if (info.asset is Texture) continue;
            if (info.asset is Material mat && IsUITexture(mat, out _)) continue;
            if (previewRequested.TryGetValue(guid, out double started) && now - started > 8)
            {
                if (now - started < 11) continue;
                previewRequested[guid] = now - 8;
            }
            if (!previewRequested.ContainsKey(guid)) previewRequested[guid] = now;
            try
            {
                Texture2D preview = AssetPreview.GetAssetPreview(info.asset);
                if (preview != null)
                {
                    StoreThumbnail(guid, CopyPreview(preview, 128));
                    previewRequested.Remove(guid);
                    Repaint();
                }
            }
            catch (Exception ex)
            {
                previewRequested[guid] = now - 9;
                Debug.LogWarning("[_Bookmark] 프리뷰 생성 실패: " + ex.Message);
            }
            if (++requests >= 2) { previewCursor = (previewCursor + i + 1) % visible.Length; break; }
        }
        // Hidden requests no longer poll, and can retry when shown again.
        foreach (string guid in previewRequested.Keys.Where(g => !visiblePreviewGuids.Contains(g)).ToList()) previewRequested.Remove(guid);
    }

    private Texture2D CopyPreview(Texture source, int maxSize)
    {
        float scale = Mathf.Min(1, (float)maxSize / Mathf.Max(source.width, source.height));
        int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale)), h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        Texture2D copy = null;
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            copy = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply(false, false);
            return copy;
        }
        catch { if (copy != null) DestroyImmediate(copy); throw; }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
    }

    private void StoreThumbnail(string guid, Texture2D texture)
    {
        if (thumbnailCache.TryGetValue(guid, out var old) && old != null && old != texture) DestroyImmediate(old);
        thumbnailCache[guid] = texture;
        previewTouched[guid] = EditorApplication.timeSinceStartup;
        missingCustomThumbnails.Remove(guid);
        while (thumbnailCache.Count > 192)
        {
            string oldest = thumbnailCache.Keys.OrderBy(g => previewTouched.TryGetValue(g, out var t) ? t : 0).First();
            if (thumbnailCache[oldest] != null) DestroyImmediate(thumbnailCache[oldest]);
            thumbnailCache.Remove(oldest); previewTouched.Remove(oldest);
        }
    }

    private void ClearPreviews()
    {
        foreach (var texture in thumbnailCache.Values) if (texture != null) DestroyImmediate(texture);
        thumbnailCache.Clear(); previewTouched.Clear(); previewRequested.Clear(); miniThumbnails.Clear();
        visiblePreviewGuids.Clear(); missingCustomThumbnails.Clear();
    }

    private List<string> MatchingTags(Material material)
    {
        var names = new List<string> { material.name };
        // Includes _MainTex, masks, noise, and every texture property exposed by the shader.
        foreach (string property in material.shader == null ? new string[0] : material.GetTexturePropertyNames())
        {
            Texture texture = material.GetTexture(property);
            if (texture != null) names.Add(texture.name);
        }
        return tagDict[AssetGroupType.Material].Where(tag => !string.IsNullOrWhiteSpace(tag.name) &&
        names.Any(name => name.IndexOf(tag.name, StringComparison.OrdinalIgnoreCase) >= 0))
        .Select(tag => tag.name).Distinct().ToList();
    }

    private int AddMatchingTags(FavoriteAsset fav, Material material)
    {
        int added = 0;
        foreach (string tag in MatchingTags(material))
        if (!fav.tags.Contains(tag)) { fav.tags.Add(tag); added++; }
        return added;
    }

    private void StartCleanup()
    {
        if (loadFailed || batchKind != BatchKind.None) return;
        if (!EditorUtility.DisplayDialog("현재 프로젝트 기준 JSON 정리",
        "열려 있는 프로젝트: " + Application.dataPath + "\n\n모든 그룹에서 현재 프로젝트에 없는 북마크를 JSON에서 제거합니다. 다른 프로젝트 전용 북마크도 제거됩니다.\n실제 에셋과 태그 정의는 삭제하지 않습니다. 정리 전 JSON을 별도로 백업합니다.", "백업 후 정리", "취소")) return;
        if (!SaveData()) return;
        try
        {
            cleanupBackup = savePath + ".cleanup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".bak";
            File.Copy(savePath, cleanupBackup, false);
        }
        catch (Exception ex) { saveError = "정리 전 백업 실패: " + ex.Message; return; }
        BeginBatch(BatchKind.Cleanup, groupDict.Values.SelectMany(g => g.assets));
    }

    private void StartAutoTags()
    {
        if (loadFailed || batchKind != BatchKind.None) return;
        BeginBatch(BatchKind.AutoTags, groupDict[AssetGroupType.Material].assets);
    }

    private void BeginBatch(BatchKind kind, IEnumerable<FavoriteAsset> assets)
    {
        batchQueue.Clear(); cleanupTargets.Clear(); autoTagResults.Clear();
        foreach (var fav in assets) batchQueue.Enqueue(fav);
        batchKind = kind;
        Repaint();
    }

    private void TickBatch()
    {
        double until = EditorApplication.timeSinceStartup + 0.004;
        int processed = 0;
        try
        {
            while (batchQueue.Count > 0 && processed++ < 64 && EditorApplication.timeSinceStartup < until)
            {
                var fav = batchQueue.Dequeue();
                string path = AssetDatabase.GUIDToAssetPath(fav.guid);
                if (batchKind == BatchKind.Cleanup)
                {
                    // GUID lookup checks presence; do not load/render every asset just to clean JSON.
                    if (string.IsNullOrEmpty(path) || AssetDatabase.GetMainAssetTypeAtPath(path) == null) cleanupTargets.Add(fav);
                }
                else
                {
                    Material mat = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat != null) autoTagResults[fav] = MatchingTags(mat);
                }
            }
            if (batchQueue.Count > 0) return;
            BatchKind finished = batchKind;
            int changed = 0;
            if ((finished == BatchKind.Cleanup && cleanupTargets.Count > 0) ||
            (finished == BatchKind.AutoTags && autoTagResults.Any(k => k.Value.Any(t => !k.Key.tags.Contains(t)))))
            {
                SaveStateToUndo();
                if (finished == BatchKind.Cleanup)
                foreach (var group in groupDict.Values) changed += group.assets.RemoveAll(a => cleanupTargets.Contains(a));
                else
                foreach (var kv in autoTagResults)
                foreach (string tag in kv.Value)
                if (!kv.Key.tags.Contains(tag)) { kv.Key.tags.Add(tag); changed++; }
                Changed();
            }
            batchKind = BatchKind.None;
            bool saved = SaveData();
            if (finished == BatchKind.Cleanup)
            {
                LoadCustomThumbnails(); RestartIndex();
                Debug.Log("[_Bookmark] JSON 정리: " + changed + "개 제거. 백업: " + cleanupBackup);
            }
            if (saved) ShowNotification(new GUIContent(finished == BatchKind.Cleanup ? changed + "개 북마크 정리 완료" : changed + "개 태그 추가 완료"));
            batchQueue.Clear(); cleanupTargets.Clear(); autoTagResults.Clear();
            Repaint();
        }
        catch (Exception ex)
        {
            batchKind = BatchKind.None;
            batchQueue.Clear(); cleanupTargets.Clear(); autoTagResults.Clear();
            saveError = "일괄 처리 실패: " + ex.Message;
            Debug.LogError("[_Bookmark] " + saveError);
            Repaint();
        }
    }

    private void TrimUndoHistory()
    {
        // Full snapshots can grow large. Bound history by both operation count and entry count.
        var newest = undoStack.ToArray();
        int total = 0, keep = 0;
        foreach (var state in newest)
        {
            int entries = state.groups.Sum(g => g.assets.Count + g.assets.Sum(a => a.tags.Count));
            if (keep > 0 && (keep >= 20 || total + entries > 200000)) break;
            total += entries; keep++;
        }
        undoStack = new Stack<SaveWrapper>(newest.Take(keep).Reverse());
    }

}
