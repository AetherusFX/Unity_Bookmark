Version	   1.3

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

### [v1.2]_251102
- Thumbnail Capture Separation & Improvement: Prefab captures now use the original SceneView-based method, while Scene captures use the new UI-camera-inclusive approach. → UI overlays (including Canvas elements) are now properly rendered in scene captures.

- Button Behavior: The “⦿” capture button automatically selects the appropriate capture method depending on whether the item is a Prefab or a Scene.

### [v1.3]_251117
- Added automatic screenshot-based thumbnail generation for Prefabs and introduced dual Prefab capture modes.