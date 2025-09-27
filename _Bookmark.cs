/*
@name: _Bookmark
@version: 1.1

Copyright (c) 2025 SominicWorks

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
using System.Linq;
using System.IO;


public class _Bookmark : EditorWindow
{
    public enum AssetGroupType { Material, Texture, Mesh, Prefab, Scene, Shader }

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
    private string savePath => @"D:\\00_PresetBackup\\@Unity\\@Editor_Json\\_BookmarkData.json";
    private bool showTags = false;
    private bool autoSave = true;
	
	private string highlightGuid = null;
	private double highlightStartTime = 0;
	
	private string scrollToGuid = null;
	
	private Stack<SaveWrapper> undoStack = new();
	private Stack<SaveWrapper> redoStack = new();

	private string thumbSaveRoot => @"D:\00_PresetBackup\@Unity\@Editor_Json\_BookmarkData_Thumbnail";
	private Dictionary<string, string> customThumbnailMap = new(); // GUID -> 썸네일 파일 경로 (Prefab/Scene 공통)

private double nextSaveTime = -1;  // 🔹 저장 예약용 변수


    [MenuItem("Tools/@FX_Tools/_Bookmark")]
    public static void ShowWindow()
{
    var window = GetWindow<_Bookmark>();
    window.titleContent = new GUIContent("_Bookmark");
    window.Show();
}

    private void OnEnable()
{
    LoadData();
    foreach (AssetGroupType type in System.Enum.GetValues(typeof(AssetGroupType)))
    {
        if (!groupDict.ContainsKey(type)) groupDict[type] = new FavoriteGroup { groupType = type };
        if (!tagDict.ContainsKey(type)) tagDict[type] = new List<TagInfo>();
    }
    if (!Directory.Exists(thumbSaveRoot)) Directory.CreateDirectory(thumbSaveRoot);
    LoadCustomThumbnails();

}

private void LoadCustomThumbnails()
{
    customThumbnailMap.Clear();
    if (!Directory.Exists(thumbSaveRoot)) return;

    var prefabGuids = new HashSet<string>(groupDict[AssetGroupType.Prefab].assets.Select(a => a.guid));
    var sceneGuids  = new HashSet<string>(groupDict[AssetGroupType.Scene].assets.Select(a => a.guid));

    foreach (var file in Directory.GetFiles(thumbSaveRoot, "*.png"))
    {
        string guid = Path.GetFileNameWithoutExtension(file);
        // Prefab 또는 Scene에 등록된 GUID만 매핑
        if (prefabGuids.Contains(guid) || sceneGuids.Contains(guid))
            customThumbnailMap[guid] = file;
    }
}

private void CaptureSceneViewToThumbnail(string prefabName, string guid)
{
    var sceneView = SceneView.lastActiveSceneView;
    if (sceneView == null)
    {
        EditorUtility.DisplayDialog("Error", "씬 뷰가 열려 있어야 합니다.", "OK");
        return;
    }

    int width = 256, height = 256;
    RenderTexture rt = new RenderTexture(width, height, 24);
    Texture2D screenShot = new Texture2D(width, height, TextureFormat.RGB24, false);

    // ✅ 씬뷰 강제 갱신
    sceneView.Repaint();
    sceneView.SendEvent(EditorGUIUtility.CommandEvent("RefreshSceneView"));

    // ✅ SceneView 카메라로 렌더링
    var cam = sceneView.camera;
    if (cam == null)
    {
        EditorUtility.DisplayDialog("Error", "씬 뷰 카메라를 찾을 수 없습니다.", "OK");
        return;
    }

    cam.targetTexture = rt;
    cam.Render();
    RenderTexture.active = rt;

    screenShot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
    screenShot.Apply();

    cam.targetTexture = null;
    RenderTexture.active = null;
    Object.DestroyImmediate(rt);

    byte[] bytes = screenShot.EncodeToPNG();
    string savePath = Path.Combine(thumbSaveRoot, guid + ".png");
    File.WriteAllBytes(savePath, bytes);
    AssetDatabase.Refresh();

    customThumbnailMap[guid] = savePath;
	// 🔹 새로 캡처된 썸네일은 즉시 캐시에 갱신
Texture2D newTex = new Texture2D(2, 2);
newTex.LoadImage(bytes);
newTex.Apply();
thumbnailCache[guid] = newTex;
}


private void RefreshThumbnail(string newPrefabName)
{
    string thumbnailFolder = @"D:\00_PresetBackup\@Unity\@Editor_Json\_BookmarkData_Thumbnail";
    string thumbnailPath = Path.Combine(thumbnailFolder, newPrefabName + "_Thumbnail.png");

    if (File.Exists(thumbnailPath))
    {
        byte[] fileData = File.ReadAllBytes(thumbnailPath);
        Texture2D tex = new Texture2D(2, 2);
        tex.LoadImage(fileData); // PNG → Texture2D
        tex.Apply();

        // 썸네일 Dictionary나 리스트에 Texture 다시 등록
        if (thumbnailCache.ContainsKey(newPrefabName))
            thumbnailCache[newPrefabName] = tex;
        else
            thumbnailCache.Add(newPrefabName, tex);

        // 에디터 UI 즉시 갱신
        Repaint();
    }
}

    private void OnGUI()
{
    selectedGroup = (AssetGroupType)GUILayout.Toolbar((int)selectedGroup, System.Enum.GetNames(typeof(AssetGroupType)));
    GUILayout.Space(10);

	// 가로 배치: [에셋 이름 검색] [텍스트필드]
using (new EditorGUILayout.HorizontalScope())
{
    GUILayout.Label("에셋 이름 검색", GUILayout.Width(90));
    var newKeyword = EditorGUILayout.TextField(searchKeyword, GUILayout.ExpandWidth(true));
    if (newKeyword != searchKeyword)
    {
        searchKeyword = newKeyword;
        Repaint(); // (선택) 입력 시 즉시 갱신
    }
}
GUILayout.Space(5);

	DrawSettingsTags();
    GUILayout.Space(10);
    DrawTagFilter();
    GUILayout.Space(10);
    DrawAssetList();
    GUILayout.Space(10);
	DrawDragArea();

}

    private void DrawTagFilter()
    {
        GUILayout.Label("태그 필터", EditorStyles.boldLabel);
        if (!tagDict.TryGetValue(selectedGroup, out var filterTags)) { GUILayout.Label("(태그 없음)"); return; }

        GUILayout.BeginVertical("box");
        
        // 현재 뷰의 사용 가능한 너비 계산 (마진 고려)
        float viewWidth = EditorGUIUtility.currentViewWidth - 40; 
        float currentLineLength = 0f;
        float spacing = 5f; // 버튼 사이의 간격
        
        // 첫 번째 행 시작
        EditorGUILayout.BeginHorizontal();

        foreach (var tag in filterTags.OrderBy(t => t.name))
        {
            bool isSelected = selectedTags.Contains(tag.name);
            GUIContent content = new GUIContent(tag.name);
            Vector2 size = GUI.skin.button.CalcSize(content);

            // 줄 바꿈 필요 시: 현재 행을 닫고 새로운 행 시작
            // currentLineLength != 0 조건을 추가하여 버튼 하나가 viewWidth보다 크더라도 무한 루프에 빠지지 않게 함
            if (currentLineLength + size.x + spacing > viewWidth && currentLineLength != 0)
            {
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                currentLineLength = 0f;
            }

            // 버튼 그리기
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = tag.color.ToColor();

            // GUILayout.Button을 사용하여 Unity의 레이아웃 시스템에 맡김
            if (GUILayout.Button(content, GUILayout.Width(size.x))) 
            {
                if (isSelected) selectedTags.Remove(tag.name);
                else selectedTags.Add(tag.name);
            }
            GUI.backgroundColor = prev;
            
            // 선택된 태그의 윤곽선 그리기
            if (isSelected) DrawOutline(GUILayoutUtility.GetLastRect());

            currentLineLength += size.x + spacing;
        }

        EditorGUILayout.EndHorizontal(); // 마지막 행 닫기
        GUILayout.EndVertical(); // 태그 박스 닫기
    }

    private void DrawSettingsTags()
{
    GUIStyle boldFoldout = new GUIStyle(EditorStyles.foldout);
    boldFoldout.fontStyle = FontStyle.Bold;

    showTags = EditorGUILayout.Foldout(showTags, "태그 추가 및 관리", true, boldFoldout);
    if (!showTags) return;

    GUILayout.BeginHorizontal();

    // ✅ 포커스 이름 설정
    GUI.SetNextControlName("NewTagField");

    // ✅ DelayedTextField → TextField (바로 값 반영)
    newTagName = EditorGUILayout.TextField(newTagName);

    // ✅ Enter 키 이벤트 처리
    if (Event.current.isKey &&
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

    // ✅ 태그 목록 출력
    foreach (var tag in tagDict[selectedGroup].OrderBy(t => t.name).ToList())
    {
        GUILayout.BeginHorizontal();

        string prevName = tag.name;
        string updatedName = EditorGUILayout.TextField(prevName);

        if (updatedName != prevName && !string.IsNullOrEmpty(updatedName))
        {
            if (!tagDict[selectedGroup].Any(t => t != tag && t.name == updatedName))
            {
                tag.name = updatedName;

                // 태그 이름 업데이트
                foreach (var asset in groupDict[selectedGroup].assets)
                {
                    for (int i = 0; i < asset.tags.Count; i++)
                    {
                        if (asset.tags[i] == prevName)
                            asset.tags[i] = updatedName;
                    }
                }
            }
        }

        // 색상 설정
        Color newColor = EditorGUILayout.ColorField(tag.color.ToColor(), GUILayout.Width(60));
        if (newColor != tag.color.ToColor())
        {
            tag.color = new SerializableColor(newColor);
        }

        // 삭제 버튼
        if (GUILayout.Button("X", GUILayout.Width(20)))
        {
            tagDict[selectedGroup].Remove(tag);
            foreach (var asset in groupDict[selectedGroup].assets)
                asset.tags.RemoveAll(t => t == tag.name);

            SaveData();
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();
    }
}

private void AddNewTag()
{
    if (!string.IsNullOrWhiteSpace(newTagName) &&
        !tagDict[selectedGroup].Any(t => t.name == newTagName))
    {
        tagDict[selectedGroup].Add(new TagInfo { name = newTagName });
        if (autoSave)
{
    nextSaveTime = EditorApplication.timeSinceStartup + 0.5f;
    EditorApplication.update -= DelayedSave;
    EditorApplication.update += DelayedSave;
}
    }
    newTagName = string.Empty;
    GUI.FocusControl(null); // 포커스 해제
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
    int columns = 2;

    if (!string.IsNullOrEmpty(scrollToGuid))
    {
        int scrollToIndex = list.FindIndex(a => a.guid == scrollToGuid);
        scrollToGuid = null;
        if (scrollToIndex >= 0)
        {
            int rowIndex = scrollToIndex / columns;
            float cardHeight = 140f;
            float targetY = rowIndex * cardHeight;
            scrollPos.y = targetY;
        }
    }

    // ⭐ 1. 버튼 그룹을 스크롤 뷰 위에 고정
    GUILayout.BeginHorizontal();
    GUILayout.Label($"{selectedGroup} 그룹", EditorStyles.boldLabel);
    GUIStyle iconButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 24,
        alignment = TextAnchor.MiddleCenter
    };
    GUIStyle saveButtonStyle = new GUIStyle(GUI.skin.button)
    {
        fontSize = 12,
        alignment = TextAnchor.MiddleCenter
    };

    Color prevColor = GUI.backgroundColor;
    GUI.backgroundColor = new Color(1.0f, 0.4f, 0.4f);
    if (GUILayout.Button("☰Json", saveButtonStyle, GUILayout.Width(70), GUILayout.Height(25)))
    {
        if (File.Exists(savePath))
            EditorUtility.RevealInFinder(savePath);
        else
        {
            string folder = Path.GetDirectoryName(savePath);
            if (Directory.Exists(folder)) EditorUtility.RevealInFinder(folder);
        }
    }
    if (GUILayout.Button("↶", iconButtonStyle, GUILayout.Width(30), GUILayout.Height(25))) Undo();
    if (GUILayout.Button("↷", iconButtonStyle, GUILayout.Width(30), GUILayout.Height(25))) Redo();
    if (GUILayout.Button("설정 저장", saveButtonStyle, GUILayout.Width(70), GUILayout.Height(25))) SaveData();

    GUI.backgroundColor = prevColor;
    GUILayout.EndHorizontal();
    // ⭐ 2. 스크롤 뷰 시작
    scrollPos = EditorGUILayout.BeginScrollView(scrollPos);


    float totalMargin = 55f;
    float cardWidth = (EditorGUIUtility.currentViewWidth - totalMargin) / columns;

    // 1) 먼저 필터링된 목록을 만든다 (여기서 continue 사용 금지)
var filtered = new List<FavoriteAsset>();
for (int i = 0; i < list.Count; i++)
{
    var fav = list[i];
    var path = AssetDatabase.GUIDToAssetPath(fav.guid);
    var obj  = AssetDatabase.LoadAssetAtPath<Object>(path);

    bool pass =
        obj != null &&
        (string.IsNullOrEmpty(searchKeyword) ||
         obj.name.ToLowerInvariant().Contains(searchKeyword.ToLowerInvariant())) &&
        (selectedTags.Count == 0 || fav.tags.Any(t => selectedTags.Contains(t)));

    if (pass) filtered.Add(fav);
}

// 2) 필터링된 목록을 기준으로 행을 정확히 열고 닫는다
for (int idx = 0; idx < filtered.Count; idx++)
{
    var fav  = filtered[idx];
    var path = AssetDatabase.GUIDToAssetPath(fav.guid);
    var obj  = AssetDatabase.LoadAssetAtPath<Object>(path);

    if (idx % columns == 0)
        EditorGUILayout.BeginHorizontal();

    // ===== 여기부터 '카드 그리기' 본문은 기존 그대로 복사 =====
    GUILayout.BeginVertical("box", GUILayout.Width(cardWidth));
    EditorGUILayout.BeginHorizontal();

    // Drag icon
    Rect dragRect = GUILayoutUtility.GetRect(16, 64, GUILayout.Width(16), GUILayout.Height(64));
    EditorGUI.LabelField(dragRect, new GUIContent("≡"), new GUIStyle(EditorStyles.label)
    {
        alignment = TextAnchor.MiddleCenter,
        fontSize = 12
    });

    if (Event.current.type == EventType.MouseDown && dragRect.Contains(Event.current.mousePosition))
    {
        DragAndDrop.PrepareStartDrag();
        DragAndDrop.SetGenericData("DraggedItem", fav);
        DragAndDrop.StartDrag("Drag");
        Event.current.Use();
    }
    if (Event.current.type == EventType.DragUpdated && dragRect.Contains(Event.current.mousePosition))
    {
        DragAndDrop.visualMode = DragAndDropVisualMode.Move;
        Event.current.Use();
    }
    if (Event.current.type == EventType.DragPerform && dragRect.Contains(Event.current.mousePosition))
    {
        var dragged = DragAndDrop.GetGenericData("DraggedItem") as FavoriteAsset;
        if (dragged != null && dragged != fav)
        {
            list.Remove(dragged);
            // filtered의 인덱스(idx)를 list의 인덱스로 역변환하기 어렵기 때문에
            // 안전하게 현재 fav의 list상 인덱스 위치로 삽입
            int insertAt = list.FindIndex(a => a.guid == fav.guid);
            if (insertAt < 0) insertAt = list.Count;
            list.Insert(insertAt, dragged);
            if (autoSave) SaveData();
        }
        DragAndDrop.SetGenericData("DraggedItem", null);
        DragAndDrop.AcceptDrag();
        Event.current.Use();
    }

    string objGuid = fav.guid;
    Texture2D tex = null;

    // 1. Prefab/Scene 커스텀 썸네일 (캐싱 추가)
if ((selectedGroup == AssetGroupType.Prefab || selectedGroup == AssetGroupType.Scene) &&
    customThumbnailMap.TryGetValue(objGuid, out var customThumbPath) && File.Exists(customThumbPath))
{
    if (!thumbnailCache.TryGetValue(objGuid, out tex) || tex == null)
    {
        byte[] fileData = File.ReadAllBytes(customThumbPath);
        tex = new Texture2D(2, 2);
        tex.LoadImage(fileData);
        tex.Apply();
        thumbnailCache[objGuid] = tex; // ✅ 캐싱
    }
}
    // 2. UI 머티리얼 mainTexture
    else if (obj is Material mat && mat.shader != null &&
             mat.shader.name.ToLowerInvariant().Contains("ui") && mat.mainTexture is Texture2D tex2D)
    {
        tex = tex2D;
    }
    // 3. 기본 프리뷰
    else
    {
        var preview = AssetPreview.GetAssetPreview(obj) ?? AssetPreview.GetMiniThumbnail(obj);
        tex = preview as Texture2D;
    }

    var previewRect = GUILayoutUtility.GetRect(64, 64, GUILayout.Width(64), GUILayout.Height(64));
    if (tex != null)
    {
        GUI.DrawTexture(previewRect, tex, ScaleMode.ScaleToFit);
        if (Event.current.type == EventType.MouseDown && previewRect.Contains(Event.current.mousePosition))
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new Object[] { obj };
            DragAndDrop.StartDrag("Dragging " + obj.name);
            Event.current.Use();
        }
    }

    if (obj is Material matCheck && matCheck.shader != null && matCheck.shader.name.ToLowerInvariant().Contains("ui"))
        DrawOutline(previewRect, new Color(1.00f, 1.00f, 0.00f, 0.70f));

    if (fav.tags.Contains("사용X"))
    {
        Handles.BeginGUI();
        Handles.color = Color.red;
        float thickness = 2f;
        Vector3 topLeft = new Vector3(previewRect.xMin, previewRect.yMin);
        Vector3 bottomRight = new Vector3(previewRect.xMax, previewRect.yMax);
        Vector3 topRight = new Vector3(previewRect.xMax, previewRect.yMin);
        Vector3 bottomLeft = new Vector3(previewRect.xMin, previewRect.yMax);
        Handles.DrawAAPolyLine(thickness, topLeft, bottomRight);
        Handles.DrawAAPolyLine(thickness, topRight, bottomLeft);
        Handles.EndGUI();
    }

    if (fav.guid == highlightGuid && EditorApplication.timeSinceStartup - highlightStartTime < 1.5f)
    {
        float t = (float)(EditorApplication.timeSinceStartup - highlightStartTime);
        float alpha = Mathf.Sin(t * Mathf.PI * 2) * 0.5f + 0.5f;
        Color glow = Color.Lerp(Color.white, Color.cyan, alpha);
        DrawOutline(previewRect, glow);
        Repaint();
    }

    GUILayout.Space(8);
    GUILayout.BeginVertical(GUILayout.ExpandWidth(true));

    EditorGUILayout.BeginHorizontal();
    EditorGUILayout.ObjectField(obj, typeof(Object), false);
    if (GUILayout.Button("X", GUILayout.Width(20)))
    {
        SaveStateToUndo();
        list.Remove(fav);
        if (autoSave) SaveData();
        GUIUtility.ExitGUI();
    }
    EditorGUILayout.EndHorizontal();

    if (obj is Material shaderMat && shaderMat.shader != null)
    {
        string shaderName = shaderMat.shader.name;
        GUIStyle shaderStyle = new GUIStyle(EditorStyles.label) 
        { 
            fontSize = 10,
            wordWrap = true // 👈 줄 바꿈 활성화
        };

        shaderStyle.normal.textColor =
            shaderName.ToLowerInvariant().Contains("flowdistortion") ? new Color(0.88f, 0.52f, 1f) :
            shaderName.ToLowerInvariant().Contains("additive")       ? Color.yellow :
            shaderName.ToLowerInvariant().Contains("alpha")          ? Color.cyan :
                                                                        new Color(0.75f, 0.75f, 0.75f);
        
        // GUILayout.Label은 GUILayout.ExpandHeight(true)와 함께 사용하지 않아도 자동으로 필요한 높이를 차지합니다.
        GUILayout.Label(shaderName, shaderStyle);
    }

    float x = 0, y = 0, tagHeight = 18, margin = 4;
    float tagAreaWidth = cardWidth - 50 - 32; // v1.0의 근사치 너비 (ObjectField 및 X 버튼 공간 제외)
    Rect tagStart = GUILayoutUtility.GetRect(tagAreaWidth, 0);

    float width = tagAreaWidth; // 👈 v1.0과 동일하게 명시적 너비 사용

    Rect arrowRect = new Rect(tagStart.x + x, tagStart.y + y, 20, tagHeight);
    if (EditorGUI.DropdownButton(arrowRect, new GUIContent("▾"), FocusType.Passive, EditorStyles.popup))
    {
        var sortedTags = tagDict[selectedGroup].OrderBy(t => t.name).ToList();
        float totalHeight = Mathf.Min(400, sortedTags.Count * 24 + 10);
        PopupWindow.Show(arrowRect, new TagPopupPicker(fav, sortedTags, autoSave, totalHeight));
    }
    x += 24;
    foreach (var tagName in fav.tags.OrderBy(n => n))
    {
        var tagInfo = tagDict[selectedGroup].FirstOrDefault(t => t.name == tagName);
        if (tagInfo == null) continue;
        Vector2 size = GUI.skin.box.CalcSize(new GUIContent(tagName));
        if (x + size.x > width) { x = 0; y += tagHeight + margin; }
        Rect rect = new Rect(tagStart.x + x, tagStart.y + y, size.x, tagHeight);
        x += size.x + margin;
        Color prevClr = GUI.backgroundColor;
        GUI.backgroundColor = tagInfo.color.ToColor();
        GUIStyle style = new GUIStyle(GUI.skin.box)
        {
            normal = { textColor = Color.white },
            alignment = TextAnchor.MiddleCenter,
            fontSize = 10
        };
        GUI.Box(rect, tagName, style);
        GUI.backgroundColor = prevClr;
    }

    if (fav.tags.Count > 0)
        GUILayout.Space(y + tagHeight + margin);

    GUILayout.EndVertical();

    EditorGUILayout.EndHorizontal();
    GUILayout.EndVertical();

    // CA 버튼 (썸네일 캡처)
    if (selectedGroup == AssetGroupType.Prefab || selectedGroup == AssetGroupType.Scene)
    {
        Rect cardRect = GUILayoutUtility.GetLastRect();
        float btnW = 26f, btnH = 16f;
        float btnX = cardRect.xMax - btnW - 4;
        float btnY = cardRect.yMax - btnH - 4;
        Rect caBtnRect = new Rect(btnX, btnY, btnW, btnH);

        if (GUI.Button(caBtnRect, "⦿"))
        {
            CaptureSceneViewToThumbnail(obj.name, fav.guid);
            LoadCustomThumbnails();
            Repaint();
        }
    }

    if (idx % columns == columns - 1)
        EditorGUILayout.EndHorizontal();
}

// 3) 마지막 행이 열려 있으면 닫아준다
if (filtered.Count > 0 && filtered.Count % columns != 0)
    EditorGUILayout.EndHorizontal();


    EditorGUILayout.EndScrollView();
}

private class TagPopupPicker : PopupWindowContent
{
    private FavoriteAsset fav;
    private List<TagInfo> tags;
    private bool autoSave;
    private float height;

    public TagPopupPicker(FavoriteAsset f, List<TagInfo> t, bool save, float customHeight = 200)
    {
        fav = f;
        tags = t;
        autoSave = save;
        height = Mathf.Min(customHeight, t.Count * 24 + 10); // 최대 높이 계산으로 불필요한 공간 제거
    }

    public override Vector2 GetWindowSize() => new(200, height);

    public override void OnGUI(Rect rect)
    {
        foreach (var tag in tags.OrderBy(t => t.name))
        {
            EditorGUILayout.BeginHorizontal();
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = tag.color.ToColor();
            GUILayout.Box("", GUILayout.Width(12), GUILayout.Height(12));
            GUI.backgroundColor = prev;
            bool has = fav.tags.Contains(tag.name);
            bool sel = EditorGUILayout.ToggleLeft(tag.name, has);
            if (sel != has)
            {
                if (sel) fav.tags.Add(tag.name);
                else fav.tags.Remove(tag.name);
                if (autoSave && editorWindow is _Bookmark tool) tool.SaveData();
            }
            EditorGUILayout.EndHorizontal();
        }
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
    if (area.Contains(Event.current.mousePosition) && (Event.current.type == EventType.DragUpdated || Event.current.type == EventType.DragPerform))
    {
        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

        if (Event.current.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            var grp = groupDict[selectedGroup];

            foreach (var obj in DragAndDrop.objectReferences)
{
    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(obj));

    // ✅ 자동 그룹 분류
    AssetGroupType targetGroup = DetectAssetGroup(obj);

    // ✅ 🔥 탭 자동 전환 추가
    selectedGroup = targetGroup;

    var targetGrp = groupDict[targetGroup];

    // ✅ 중복이면 하이라이트
    if (targetGrp.assets.Any(a => a.guid == guid))
    {
        highlightGuid = guid;
        highlightStartTime = EditorApplication.timeSinceStartup;
        scrollToGuid = guid;
        continue;
    }

    // ✅ 추가
    targetGrp.assets.Add(new FavoriteAsset { guid = guid });
}

if (autoSave) SaveData();
        }

        Event.current.Use();
    }
}

private void DelayedSave()
{
    if (nextSaveTime > 0 && EditorApplication.timeSinceStartup >= nextSaveTime)
    {
        nextSaveTime = -1;
        EditorApplication.update -= DelayedSave;
        SaveData();  // 🔹 실제 저장 실행
    }
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


    return selectedGroup; // 🔸 기본값: 인식 불가하면 현재 선택된 그룹
}

    private class TagPicker : PopupWindowContent
    {
        private FavoriteAsset fav;
        private List<TagInfo> tags;
        private bool autoSave;
        private Vector2 scroll;
        public TagPicker(FavoriteAsset f, List<TagInfo> t, bool save) { fav = f; tags = t; autoSave = save; }
        public override Vector2 GetWindowSize() => new(200, Mathf.Min(200, tags.Count * 24 + 10));
        public override void OnGUI(Rect rect)
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var tag in tags)
            {
                EditorGUILayout.BeginHorizontal();
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = tag.color.ToColor();
                GUILayout.Box("", GUILayout.Width(12), GUILayout.Height(12));
                GUI.backgroundColor = prev;
                bool has = fav.tags.Contains(tag.name);
                bool sel = EditorGUILayout.ToggleLeft(tag.name, has);
                if (sel != has)
                {
                    if (sel) fav.tags.Add(tag.name);
                    else fav.tags.Remove(tag.name);
                    if (autoSave && editorWindow is _Bookmark tool) tool.SaveData();
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
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
    redoStack.Clear();
}

	private void Undo()
	{
		if (undoStack.Count == 0) return;
		SaveWrapper current = GetCurrentState();
		redoStack.Push(current);
		ApplyState(undoStack.Pop());
		SaveData();
	}

	private void Redo()
	{
		if (redoStack.Count == 0) return;
		SaveWrapper current = GetCurrentState();
		undoStack.Push(current);
		ApplyState(redoStack.Pop());
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
	}

    private void SaveData()
    {
        var wrapper = new SaveWrapper { groups = groupDict.Values.ToList(), tagGroups = tagDict.Select(kvp => new TagGroup { groupType = kvp.Key, tags = kvp.Value }).ToList() };
        Directory.CreateDirectory(Path.GetDirectoryName(savePath));
        File.WriteAllText(savePath, JsonUtility.ToJson(wrapper, true));
    }

    private void LoadData()
    {
        if (!File.Exists(savePath)) return;
        var wrapper = JsonUtility.FromJson<SaveWrapper>(File.ReadAllText(savePath));
        groupDict = wrapper.groups.ToDictionary(g => g.groupType, g => g);
        tagDict = wrapper.tagGroups.ToDictionary(x => x.groupType, x => x.tags);
    }

    public static List<FavoriteGroup> GetCurrentFavorites(out Dictionary<AssetGroupType, List<TagInfo>> tags)
    {
        var tool = CreateInstance<_Bookmark>();
        tool.LoadData();
        tags = tool.tagDict;
        return tool.groupDict.Values.ToList();
    }
}