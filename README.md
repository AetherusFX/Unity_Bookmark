Version	   1.1

## 목적
유니티 프로젝트 에셋(Material, Texture, Prefab 등)을 그룹별로 관리하고, 태그 및 검색 기능을 통해 쉽게 찾을 수 있게 돕는 에디터 확장 도구입니다.

## 추가설명
이 에디터 확장 도구는 저의 개인적인 요구 사항을 바탕으로 제작되었습니다.
따라서 적극적인 업데이트나 개별적인 지원 요청에 대한 응답이 어려울 수 있습니다. 
필요한 추가 기능이 있다면, 본 플러그인은 GitHub에서 무료로 다운로드 가능하니 자유롭게 수정하여 사용해 주시기 바랍니다.
MIT License (Modified Version)

## 사용법 가이드
- 00_PresetBackup		D 드라이브에 추가합니다.
- _Bookmark.cs		유니티의 Editor 폴더 내부에 추가합니다. (예: 저는 Editor 내에 @Editor 폴더를 새로 만들어 그 안에 넣었으나, 필수는 아닙니다.)

### [v1.0]
- 에셋 드래그 앤 드롭으로 즐겨찾기 등록 및 그룹 자동 분류
- 태그 기반 필터링 및 관리 (태그별 색상 지원) 
- 에셋 이름 검색 기능 
- Prefab/Scene 에셋의 씬 뷰 캡처를 통한 커스텀 썸네일 생성 및 캐싱 
- 북마크 리스트 내에서 에셋 드래그하여 순서 변경 
- Undo/Redo 기능 및 수동/자동 저장 기능

### [v1.1]_250927	
- UI/사용성 개선: UI 레이아웃을 고정하여 스크롤 시 버튼이 함께 움직이는 현상을 수정하고, 에셋 목록 카드의 너비를 창 크기에 맞춰 가로 스크롤을 제거했습니다. 
- UI 머티리얼 표시: UI 쉐이더를 사용하는 머티리얼 프리뷰에 노란색 테두리의 불투명도를 0.7로 조정합니다. 
- 머티리얼 정보: 머티리얼 카드의 쉐이더 이름을 줄 바꿈(Word Wrap) 처리합니다. 
- 성능 최적화: 저장된 에셋이 많아질 때 발생할 수 있는 느려짐 문제를 개선했습니다.


## Purpose	
An Editor Extension tool for Unity that helps users easily manage and find project assets (Material, Texture, Prefab, etc.) by grouping them and providing tag and search functionalities.

## Support
I’ve created this Editor Extension tool based on my specific needs.
Therefore, I may not be able to actively provide updates or respond to individual support requests. If you require additional features, this plugin is available for free download on GitHub, and you are welcome to freely modify and use it for your needs.
MIT License (Modified Version)

## Usage Guide
- 00_PresetBackup folder	Add this to the D drive.
- _Bookmark.cs file		Add this inside Unity's Editor folder. (e.g., I created a new folder named @FX inside the Editor folder and placed it there, but this is not mandatory.)

### [v1.0]
- Drag-and-drop asset registration with automatic group classification. 
- Tag-based filtering and management (with color support per tag). 
- Asset name search functionality. 
- Custom thumbnail creation for Prefab/Scene assets via Scene View capture and caching. 
- Reordering assets within the bookmark list via drag-and-drop. 
- Undo/Redo functionality and manual/delayed auto-save.

### [v1.1]_250927		
- UI/Usability: Fixed the issue where buttons scrolled along with the asset list by anchoring the UI layout. Eliminated horizontal scrolling by making the asset card width dynamic to fit the window size. 
- UI Material Indication: Adjusted the opacity of the yellow border for UI Shader Materials in the preview to 0.7 for better visibility. 
- Material Info: Added Word Wrap to the shader name label on the Material card for cleaner display of long shader names. 
- Performance Optimization: Improved performance to mitigate slowdowns that could occur with a large number of saved assets. 
