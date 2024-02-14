using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace ReverseGravity {
	public class SpriteAnimator : EditorWindow {
		private ReorderableList _framesReorderableList;
		private Vector2 _scrollPosition = Vector2.zero;
		private bool _settingsUnfolded;

		private int _resizeFrameId;
		private float _selectionMouseStart;

		private const float TimelineScrubberHeight = 16;
		private const float TimelineBottomBarHeight = 24;
		private const float TimelineOffsetMin = -10;

		private const float ScrubberIntervalToShowLabel = 60.0f;
		private const float ScrubberIntervalWidthMin = 10.0f;
		private const float ScrubberIntervalWidthMax = 80.0f;

		private static Color Magenta = new(1f, 0.2f, 1f);
		private static Color Clear = new(0f, 0f, 0f, 0f);
		private static Color LightGrey = new(.5f, .5f, .5f);
		private static Color Grey = new(.4f, .4f, .4f);
		private static Color GreyA20 = new(.4f, .4f, .4f, .2f);
		private static Color GreyA40 = new(.4f, .4f, .4f, .4f);
		private static Color DarkGrey = new(.3f, .3f, .3f);
		private static Color Blue = new(.3f, .5f, .85f);
		private static Color BlueA10 = new(.3f, .5f, .85f, .1f);
		private static Color BlueA60 = new(.3f, .5f, .85f, .6f);

		private const string None = "none";

		private const float FrameResizeRectWidth = 8;

		// Class used internally to store info about a frame
		[Serializable]
		private class AnimFrame {
			public float time;
			public float length;
			public Sprite sprite;

			public float EndTime => time + length;
		}

		// Static list of content (built in editor icons) & GUIStyles
		private class GUIElements {
			public static GUIStyle PreviewButton = new("preButton");

			public static GUIStyle PreviewButtonLoop = new(PreviewButton)
				{ padding = new RectOffset(0, 0, 2, 0) };

			public static GUIStyle PreviewSlider = new("preSlider");
			public static GUIStyle PreviewSliderThumb = new("preSliderThumb");
			public static GUIStyle PreviewLabelBold = new("preLabel");

			public static GUIStyle PreviewLabelSpeed =
				new("preLabel") { fontStyle = FontStyle.Normal, normal = { textColor = LightGrey } };

			public static GUIStyle TimelineAnimBg = new("CurveEditorBackground");
			public static GUIStyle TimelineBottomBarBg = new("ProjectBrowserBottomBarBg");

			public static GUIStyle InfoPanelLabelRight = new(EditorStyles.label)
				{ alignment = TextAnchor.MiddleRight };

			public static GUIContent Play = EditorGUIUtility.IconContent("PlayButton");
			public static GUIContent Pause = EditorGUIUtility.IconContent("PauseButton");
			public static GUIContent Prev = EditorGUIUtility.IconContent("Animation.PrevKey");
			public static GUIContent Next = EditorGUIUtility.IconContent("Animation.NextKey");
			public static GUIContent SpeedScale = EditorGUIUtility.IconContent("UnityEditor.ProfilerWindow");
			public static GUIContent Zoom = EditorGUIUtility.IconContent("ViewToolZoom");
			public static GUIContent LoopOff = EditorGUIUtility.IconContent("playLoopOff");
			public static GUIContent LoopOn = EditorGUIUtility.IconContent("playLoopOn");
		}

		// Store cached data for rendering sprites
		private class SpriteRenderData {
			public Mesh previewMesh;
			public Material mat;
		}

		private const string PropertyNameSprite = "m_Sprite";
		private const float TimelineHeight = 240;
		private const float CheckerboardScale = 32.0f;

		private static Texture2D _textureCheckerboard;
		private static Texture2D _bgRectTexture;
		private Color _bgColor = GreyA40;
		private bool _showCheckerboard = true;

		[SerializeField] private AnimationClip clip;
		private EditorCurveBinding _curveBinding;
		[SerializeField] private List<AnimFrame> frames;
		[SerializeField] private float infoPanelWidth = 260;

		[SerializeField] private bool playing;
		private float _animTime;
		private double _editorTimePrev;

		private float _previewSpeedScale = 1.0f;
		private float _previewScale = 1.0f;
		private Vector2 _previewOffset = Vector2.zero;
		[SerializeField] private bool previewLoop;
		private bool _previewResetScale; // When true, the preview will scale to best fit next update

		// When true, the anim plays automatically when animation is selected. Set to false when users manually stops an animation
		[SerializeField] private bool autoPlay = true;

		// Default frame length + num samples, etc
		[SerializeField] private float defaultFrameLength = 1f;
		[SerializeField] private int defaultFrameSamples = 6;

		// Timeline view's offset from left (in pixels)
		private float _timelineOffset = -TimelineOffsetMin;

		// Unit per second on timeline
		private float _timelineScale = 1000;
		private float _timelineAnimWidth = 1;

		// Repaint while drag and dropping into editor to show position indicator
		private bool _dragDropHovering = true;

		// Current drag state of mouse
		private DragState _dragState = DragState.NONE;

		// List of selected frames
		private List<AnimFrame> _selectedFrames = new();

		// List of copied frames
		[SerializeField] private List<AnimFrame> copiedFrames;

		// Used to clear selection when hit play (avoids selection references being broken)
		private bool _wasPlaying;

		// Default sprite shader to cache
		private Shader _defaultSpriteShader;

		// Stores cached data for rendering sprites
		private PreviewRenderUtility _prevRender;
		private readonly Dictionary<Sprite, SpriteRenderData> _spriteRenderData = new();

		[MenuItem("Window/Sprite Animator")]
		private static void ShowWindow() {
			GetWindow(typeof(SpriteAnimator), false);
		}

		public SpriteAnimator() {
			EditorApplication.update += Update;
			Undo.undoRedoPerformed += OnUndoRedo;
		}

		private void OnDestroy() {
			EditorApplication.update -= Update;
		}

		private void OnEnable() {
			var icon = (Texture2D)EditorGUIUtility.Load("d_Profiler.Rendering");
			titleContent = new GUIContent("Sprite Animator", icon);

			_editorTimePrev = EditorApplication.timeSinceStartup;

			// Load editor preferences
			defaultFrameLength = EditorPrefs.GetFloat("SADefFrLen", defaultFrameLength);
			defaultFrameSamples = EditorPrefs.GetInt("SADefFrSmpl", defaultFrameSamples);

			_framesReorderableList = new ReorderableList(frames, typeof(AnimFrame), true, true, true, true) {
				drawHeaderCallback = rect => {
					EditorGUI.LabelField(rect, "Frames");
					EditorGUI.LabelField(new Rect(rect) { x = rect.width - 37, width = 45 }, "Length");
				},
				drawElementCallback = LayoutFrameListFrame
			};

			_framesReorderableList.onSelectCallback = _ => { SelectFrame(frames[_framesReorderableList.index]); };

			OnSelectionChange();
		}

		private void LayoutFrameListFrame(Rect rect, int index, bool isActive, bool isFocused) {
			if (frames == null || index < 0 || index >= frames.Count) return;
			var frame = frames[index];

			EditorGUI.BeginChangeCheck();
			rect = new Rect(rect) { height = rect.height - 4, y = rect.y + 2 };

			// Frame ID
			var xOffset = rect.x;
			var width = GUIElements.InfoPanelLabelRight.CalcSize(new GUIContent(index.ToString())).x;
			EditorGUI.LabelField(new Rect(rect) { x = xOffset, width = width }, index.ToString(),
				GUIElements.InfoPanelLabelRight);

			// Frame Sprite
			xOffset += width + 5;
			width = rect.xMax - 5 - 28 - xOffset;

			// Sprite
			var spriteFieldRect = new Rect(rect) { x = xOffset, width = width, height = 16 };
			frame.sprite = EditorGUI.ObjectField(spriteFieldRect, frame.sprite, typeof(Sprite), false) as Sprite;

			// Frame length (in samples)
			xOffset += width + 5;
			width = 28;
			GUI.SetNextControlName("FrameLen");
			var minFrameTime = 1.0f / clip.frameRate;
			var frameLen = Mathf.RoundToInt(frame.length / minFrameTime);
			frameLen = EditorGUI.IntField(new Rect(rect) { x = xOffset, width = width }, frameLen);
			SetFrameLength(frame, frameLen * minFrameTime);

			if (EditorGUI.EndChangeCheck())
				// Apply events
				ApplyChanges();
		}

		private void OnDisable() {
			// Save editor preferences
			EditorPrefs.SetFloat("SADefFrLen", defaultFrameLength);
			EditorPrefs.SetInt("SADefFrSmpl", defaultFrameSamples);

			_prevRender?.Cleanup();
		}

		private void OnFocus() {
			OnSelectionChange();
		}

		private void OnGUI() {
			GUI.SetNextControlName(None);
			// If no sprite selected, show editor
			if (clip is null || frames == null) {
				GUILayout.Space(10);
				GUILayout.Label("No Animation Selected", EditorStyles.centeredGreyMiniLabel);
				return;
			}

			// Toolbar
#if UNITY_2019_3_OR_NEWER
			GUILayout.Space(2);
			GUILayout.BeginHorizontal(EditorStyles.toolbar);
#else
		GUILayout.BeginHorizontal( Styles.PREVIEW_BUTTON );
#endif
			{
				// Toolbar Play
				EditorGUI.BeginChangeCheck();
				GUILayout.Toggle(playing, playing ? GUIElements.Pause : GUIElements.Play, GUIElements.PreviewButton,
					GUILayout.Width(40));
				if (EditorGUI.EndChangeCheck()) {
					//Toggle playback
					playing = !playing;

					// Set the auto play variable. Anims will auto play when selected unless user has manually stopped an anim.
					autoPlay = playing;

					if (playing)
						// Clicked play
						// If anim is at end, restart
						if (_animTime >= GetAnimLength())
							_animTime = 0;
				}

				GUI.SetNextControlName("Toolbar");

				// Toolbar Prev
				if (GUILayout.Button(GUIElements.Prev, GUIElements.PreviewButton, GUILayout.Width(25))) {
					if (frames.Count <= 1) return;

					playing = false;
					var frame = Mathf.Clamp(GetCurrentFrameID() - 1, 0, frames.Count - 1);
					_animTime = frames[frame].time;
				}

				// Toolbar Next
				if (GUILayout.Button(GUIElements.Next, GUIElements.PreviewButton, GUILayout.Width(25))) {
					if (frames.Count <= 1) return;

					playing = false;
					var frame = Mathf.Clamp(GetCurrentFrameID() + 1, 0, frames.Count - 1);
					_animTime = frames[frame].time;
				}

				// Toolbar Loop Toggle
				previewLoop = GUILayout.Toggle(previewLoop, previewLoop ? GUIElements.LoopOn : GUIElements.LoopOff,
					GUIElements.PreviewButtonLoop, GUILayout.Width(25));

				// Speed Slider
				if (GUILayout.Button(GUIElements.SpeedScale, GUIElements.PreviewLabelBold, GUILayout.Width(30)))
					_previewSpeedScale = 1;
				_previewSpeedScale = GUILayout.HorizontalSlider(_previewSpeedScale, 0, 4, GUIElements.PreviewSlider,
					GUIElements.PreviewSliderThumb, GUILayout.Width(50));
				GUILayout.Label(_previewSpeedScale.ToString("0.00"), GUIElements.PreviewLabelSpeed,
					GUILayout.Width(40));

				// Scale Slider - Toggle scale when zoom button is pressed
				if (GUILayout.Button(GUIElements.Zoom, GUIElements.PreviewLabelBold, GUILayout.Width(30))) {
					if (_previewScale == 1) _previewResetScale = true;
					else _previewScale = 1;
				}

				_previewScale = GUILayout.HorizontalSlider(_previewScale, 1f, 12, GUIElements.PreviewSlider,
					GUIElements.PreviewSliderThumb, GUILayout.Width(50));
				GUILayout.Label(_previewScale.ToString("0.0"), GUIElements.PreviewLabelSpeed, GUILayout.Width(40));

				// Quick Scales
				if (GUILayout.Button("1x", GUIElements.PreviewButton, GUILayout.Width(40))) {
					if (_previewScale == 1) _previewResetScale = true;
					else _previewScale = 1;
				}

				if (GUILayout.Button("2x", GUIElements.PreviewButton, GUILayout.Width(40))) _previewScale = 2;
				if (GUILayout.Button("4x", GUIElements.PreviewButton, GUILayout.Width(40))) _previewScale = 4;
				if (GUILayout.Button("8x", GUIElements.PreviewButton, GUILayout.Width(40))) _previewScale = 8;

				//Toolbar Animation Name
				GUILayout.Space(10);
				if (GUILayout.Button(clip.name,
					    new GUIStyle(GUIElements.PreviewButton)
						    { stretchWidth = true, alignment = TextAnchor.MiddleLeft })) {
					Selection.activeObject = clip;
					EditorGUIUtility.PingObject(clip);
				}
			}

			GUILayout.EndHorizontal();

			// Preview
			var lastRect = GUILayoutUtility.GetLastRect();
			var previewRect = new Rect(lastRect.xMin, lastRect.yMax, position.width - infoPanelWidth,
				position.height - lastRect.yMax - TimelineHeight);
			if (_previewResetScale) {
				Sprite sprite = null;
				if (frames.Count > 0) sprite = frames[0].sprite;

				_previewScale = 1;
				if (sprite is not null && previewRect.width > 0 && previewRect.height > 0 && sprite.rect.width > 0 &&
				    sprite.rect.height > 0) {
					var widthScaled = previewRect.width / sprite.rect.width;
					var heightScaled = previewRect.height / sprite.rect.height;

					// Finds best fit for preview window based on sprite size
					if (widthScaled < heightScaled)
						_previewScale = previewRect.width / sprite.rect.width;
					else
						_previewScale = previewRect.height / sprite.rect.height;

					_previewScale = Mathf.Clamp(_previewScale, 0.1f, 100.0f) * 0.95f;

					// Set the preview offset to center the sprite
					_previewOffset = -(sprite.rect.size * 0.5f - sprite.pivot) * _previewScale;
					_previewOffset.y = -_previewOffset.y;
				}

				_previewResetScale = false;

				// Also reset timeline length
				_timelineScale = position.width / (Mathf.Max(0.5f, clip.length) * 1.25f);
			}

			// Preview
			// Draw checkerboard
			if (_showCheckerboard) {
				var f = CheckerboardScale * _previewScale;
				var texCoords = new Rect(Vector2.zero, previewRect.size / f) {
					center = new Vector2(-_previewOffset.x, _previewOffset.y) / f
				};

				if (_textureCheckerboard is not null) {
					GUI.DrawTextureWithTexCoords(previewRect, _textureCheckerboard, texCoords, false);
				}
				else {
					_textureCheckerboard = new Texture2D(2, 2) {
						hideFlags = HideFlags.DontSave,
						filterMode = FilterMode.Point,
						wrapMode = TextureWrapMode.Repeat
					};

					_textureCheckerboard.SetPixel(0, 0, Grey);
					_textureCheckerboard.SetPixel(1, 1, Grey);
					_textureCheckerboard.SetPixel(0, 1, DarkGrey);
					_textureCheckerboard.SetPixel(1, 0, DarkGrey);
					_textureCheckerboard.Apply();

					GUI.DrawTextureWithTexCoords(previewRect, _textureCheckerboard, texCoords, false);
				}
			}

			var bgTexCoords = new Rect(Vector2.zero, previewRect.size) {
				center = new Vector2(-_previewOffset.x, _previewOffset.y) / _previewScale
			};

			if (_bgRectTexture is not null) {
				GUI.DrawTextureWithTexCoords(previewRect, _bgRectTexture, bgTexCoords);
			}
			else {
				_bgRectTexture = new Texture2D(1, 1) {
					hideFlags = HideFlags.DontSave,
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Repeat
				};

				_bgRectTexture.SetPixel(0, 0, _bgColor);
				_bgRectTexture.Apply();

				GUI.DrawTextureWithTexCoords(previewRect, _bgRectTexture, bgTexCoords, false);
			}

			// Draw sprite
			Sprite previewSprite = null;
			if (frames.Count > 0) previewSprite = GetFrameAtTime(_animTime).sprite;

			if (previewSprite is not null) {
#if (UNITY_2017_1_OR_NEWER && !UNITY_2017_4_OR_NEWER) || (UNITY_2019_1_OR_NEWER && !UNITY_2019_3_OR_NEWER)
			// In 2017.1 and 2019.1 Can't display packed sprites while game is running, so don't bother trying
			if ( Application.isPlaying && (UnityEditor.EditorSettings.spritePackerMode == SpritePackerMode.AlwaysOn || UnityEditor.EditorSettings.spritePackerMode == SpritePackerMode.AlwaysOnAtlas) && sprite.packed && sprite.packingMode != SpritePackingMode.Rectangle )
			{
				EditorGUI.LabelField(rect,"Disabled in Play Mode for Packed Sprites", new GUIStyle( EditorStyles.boldLabel) { alignment
 = TextAnchor.MiddleCenter, normal = { textColor = Color.white } } );
				return;
			}
#endif
				LayoutFrameSprite(previewRect, previewSprite, _previewScale, _previewOffset, false, true);
			}

			// Handle layout events
			var e = Event.current;
			if (previewRect.Contains(e.mousePosition)) {
				if (e.type == EventType.ScrollWheel) {
					var scale = 1000.0f;
					while (_previewScale / scale < 1.0f || _previewScale / scale > 10.0f) scale /= 10.0f;

					_previewScale -= e.delta.y * scale * 0.05f;
					_previewScale = Mathf.Clamp(_previewScale, 0.1f, 100.0f);
					Repaint();
					e.Use();
				}
				else if (e.type == EventType.MouseDrag) {
					if (e.button is 1 or 2)
						if (previewSprite is not null) {
							_previewOffset += e.delta;
							Repaint();
							e.Use();
						}
				}
			}

			// Info Panel
			var infoPanelRect = new Rect(lastRect.xMin + position.width - infoPanelWidth, lastRect.yMax,
				infoPanelWidth, position.height - lastRect.yMax - TimelineHeight);
			GUILayout.BeginArea(infoPanelRect, EditorStyles.inspectorFullWidthMargins);
			GUILayout.Space(20);

			// Animation length
			EditorGUILayout.LabelField(
				$"Length: {clip.length:0.00} sec {Mathf.RoundToInt(clip.length / (1.0f / clip.frameRate)):D} samples",
				new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = LightGrey } });

			// Speed/Framerate
			GUI.SetNextControlName("Framerate");
			var newFramerate = EditorGUILayout.DelayedFloatField("Sample Rate", clip.frameRate);
			if (Mathf.Approximately(newFramerate, clip.frameRate) == false) {
				//Change FrameRate
				Undo.RecordObject(clip, "Change Framerate");

				// Scale each frame (if preserving timing) and clamp to closest sample time
				var newMinFrameTime = 1.0f / newFramerate;

				for (var i = 0; i < frames.Count; i++) {
					var frame = frames[i];
					var snapTo = Mathf.Round(frame.length / newMinFrameTime) * newMinFrameTime;
					frame.length = Mathf.Max(snapTo, newMinFrameTime);
				}

				clip.frameRate = newFramerate;
				RecalcFrameTimes();
				ApplyChanges();
			}

			GUI.SetNextControlName("Length");

			var oldLength = Mathf.Round(clip.length / 0.001f) * 0.001f;
			var newLength = Mathf.Round(EditorGUILayout.FloatField("Length (Seconds)", oldLength) / 0.001f) * 0.001f;
			if (Mathf.Approximately(newLength, oldLength) == false && newLength > 0) {
				newFramerate = Mathf.Max(Mathf.Round(clip.frameRate * (clip.length / newLength) / 1) * 1, 1);
				Undo.RecordObject(clip, "Change Framerate");

				// Scale each frame (if preserving timing) and clamp to closest sample time
				var newMinFrameTime = 1.0f / newFramerate;
				var scale = clip.frameRate / newFramerate;
				for (var i = 0; i < frames.Count; i++) {
					var frame = frames[i];
					frame.length = Mathf.Max(Mathf.Round(frame.length * scale / newMinFrameTime) * newMinFrameTime,
						newMinFrameTime);
				}

				clip.frameRate = newFramerate;
				RecalcFrameTimes();
				ApplyChanges();
			}

			// Loop tick
			var looping = EditorGUILayout.Toggle("Loop", clip.isLooping);
			if (looping != clip.isLooping) {
				Undo.RecordObject(clip, "Toggle Looping");
				var settings = AnimationUtility.GetAnimationClipSettings(clip);
				settings.loopTime = looping;
				AnimationUtility.SetAnimationClipSettings(clip, settings);

				previewLoop = looping;

				// When hitting play directly after this change, the looping state will be undone. So have to call ApplyChanges() afterwards even though frame data hasn't changed.
				ApplyChanges();
			}

			GUILayout.Space(10);

			// Frames list
			_scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, false, false);
			EditorGUI.BeginChangeCheck();
			_framesReorderableList.DoLayoutList();
			if (EditorGUI.EndChangeCheck()) {
				RecalcFrameTimes();
				Repaint();
				ApplyChanges();
			}

			_settingsUnfolded = EditorGUILayout.Foldout(_settingsUnfolded, "Settings",
				new GUIStyle(EditorStyles.foldout) { normal = { textColor = LightGrey } });

			if (_settingsUnfolded) {
				GUI.SetNextControlName("InfoPanelWidth");
				infoPanelWidth = Mathf.Max(140, EditorGUILayout.FloatField("Info Panel Width", infoPanelWidth));
				GUI.SetNextControlName("DefaultLen");
				defaultFrameLength = EditorGUILayout.DelayedFloatField("Default Frame Length", defaultFrameLength);
				GUI.SetNextControlName("DefaultSamples");
				defaultFrameSamples = EditorGUILayout.DelayedIntField("Default Frame Samples", defaultFrameSamples);
				GUI.SetNextControlName("ToggleCheckerBoard");
				_showCheckerboard = EditorGUILayout.Toggle("Show Checkerboard", _showCheckerboard);
				GUI.SetNextControlName("BGColor");
				_bgColor = EditorGUILayout.ColorField("Background Color:", _bgColor);
				if (GUILayout.Button("Apply Color")) {
					_bgRectTexture.SetPixel(0, 0, _bgColor);
					_bgRectTexture.Apply();

					GUI.DrawTextureWithTexCoords(previewRect, _bgRectTexture, bgTexCoords, false);
				}
			}

			EditorGUILayout.EndScrollView();
			GUILayout.EndArea();

			// Timeline
			var timelineRect = new Rect(0, previewRect.yMax, position.width, TimelineHeight);

			// Store mouse x offset when ever button is pressed for selection box
			if (_dragState == DragState.NONE && e.rawType == EventType.MouseDown && e.button == 0)
				_selectionMouseStart = e.mousePosition.x;

			// Select whatever is in the selection box
			if (_dragState == DragState.SELECT_FRAME && e.rawType == EventType.MouseDrag && e.button == 0) {
				var dragTimeStart = GuiPosToAnimTime(timelineRect, _selectionMouseStart);
				var dragTimeEnd = GuiPosToAnimTime(timelineRect, e.mousePosition.x);
				if (dragTimeStart > dragTimeEnd)
					(dragTimeStart, dragTimeEnd) = (dragTimeEnd, dragTimeStart);

				_selectedFrames = frames.FindAll(frame =>
					frame.time + frame.length >= dragTimeStart && frame.time <= dragTimeEnd);
				_selectedFrames.Sort((a, b) => a.time.CompareTo(b.time));

				GUI.FocusControl(None);
			}

			_timelineScale = Mathf.Clamp(_timelineScale, 10, 8000);

			// Update timeline offset
			_timelineAnimWidth = _timelineScale * GetAnimLength();
			if (_timelineAnimWidth > timelineRect.width / 2.0f)
				_timelineOffset = Mathf.Clamp(_timelineOffset,
					timelineRect.width - _timelineAnimWidth - timelineRect.width / 2.0f, -TimelineOffsetMin);
			else
				_timelineOffset = -TimelineOffsetMin;

			// Scrubber
			// Draw scrubber bar
			var elementPosY = timelineRect.yMin;
			var elementHeight = TimelineScrubberHeight;
			var scrubberRect = new Rect(timelineRect) { yMin = elementPosY, height = elementHeight };

			// Calculate time scrubber lines
			var minUnitSecond = 1.0f / clip.frameRate;
			var curUnitSecond = 1.0f;
			var curCellWidth = _timelineScale;

			var intervalScales = new List<int>();
			var tmpSampleRate = (int)clip.frameRate;
			while (true) {
				int div;
				if (tmpSampleRate == 30) div = 3;
				else if (tmpSampleRate % 2 == 0) div = 2;
				else if (tmpSampleRate % 5 == 0) div = 5;
				else if (tmpSampleRate % 3 == 0) div = 3;
				else break;

				tmpSampleRate /= div;
				intervalScales.Insert(0, div);
			}

			var intervalId = intervalScales.Count;
			intervalScales.AddRange(new[] { 5, 2, 3, 2, 5, 2, 3, 2 });

			// Get current unit secs and current index
			if (curCellWidth < ScrubberIntervalWidthMin)
				while (curCellWidth < ScrubberIntervalWidthMin) {
					curUnitSecond *= intervalScales[intervalId];
					curCellWidth *= intervalScales[intervalId];

					intervalId += 1;
					if (intervalId >= intervalScales.Count) {
						intervalId = intervalScales.Count - 1;
						break;
					}
				}
			else if (curCellWidth > ScrubberIntervalWidthMax)
				while (curCellWidth > ScrubberIntervalWidthMax && curUnitSecond > minUnitSecond) {
					intervalId -= 1;
					if (intervalId < 0) {
						intervalId = 0;
						break;
					}

					curUnitSecond /= intervalScales[intervalId];
					curCellWidth /= intervalScales[intervalId];
				}

			// Check if previous width is good to show
			if (curUnitSecond > minUnitSecond) {
				var prevIntervalID = intervalId - 1;
				if (prevIntervalID < 0) prevIntervalID = 0;
				var prevCellWidth = curCellWidth / intervalScales[prevIntervalID];
				var prevUnitSecond = curUnitSecond / intervalScales[prevIntervalID];
				if (prevCellWidth >= ScrubberIntervalWidthMin) {
					intervalId = prevIntervalID;
					curUnitSecond = prevUnitSecond;
					curCellWidth = prevCellWidth;
				}
			}

			// Get lod interval list
			var lodIntervalList = new int[intervalScales.Count + 1];
			lodIntervalList[intervalId] = 1;
			for (var i = intervalId - 1; i >= 0; --i) lodIntervalList[i] = lodIntervalList[i + 1] / intervalScales[i];

			for (var i = intervalId + 1; i < intervalScales.Count + 1; ++i)
				lodIntervalList[i] = lodIntervalList[i - 1] * intervalScales[i - 1];

			// Calculate width of intervals
			var lodWidthList = new float[intervalScales.Count + 1];
			lodWidthList[intervalId] = curCellWidth;
			for (var i = intervalId - 1; i >= 0; --i) lodWidthList[i] = lodWidthList[i + 1] / intervalScales[i];

			for (var i = intervalId + 1; i < intervalScales.Count + 1; ++i)
				lodWidthList[i] = lodWidthList[i - 1] * intervalScales[i - 1];

			// Calculate interval ID to start from
			var idxFrom = intervalId;
			for (var i = 0; i < intervalScales.Count + 1; ++i)
				if (lodWidthList[i] > ScrubberIntervalWidthMax) {
					idxFrom = i;
					break;
				}

			// +50 to avpod clip text
			var startFrom = Mathf.CeilToInt(-(_timelineOffset + 50.0f) / curCellWidth);
			var cellCount = Mathf.CeilToInt((scrubberRect.width - _timelineOffset) / curCellWidth);

			// Draw scrubber bar
			GUI.BeginGroup(scrubberRect, EditorStyles.toolbar);

			for (var i = startFrom; i < cellCount; ++i) {
				var x = _timelineOffset + i * curCellWidth + 1;
				var idx = idxFrom;

				while (idx >= 0) {
					if (i % lodIntervalList[idx] == 0) {
						var heightRatio = 1.0f - lodWidthList[idx] / ScrubberIntervalWidthMax;

						// Draw scrubber bar
						if (heightRatio >= 1.0f) {
							DrawLine(new Vector2(x, 0), new Vector2(x, TimelineScrubberHeight), LightGrey);
							DrawLine(new Vector2(x + 1, 0), new Vector2(x + 1, TimelineScrubberHeight), LightGrey);
						}
						else {
							DrawLine(new Vector2(x, TimelineScrubberHeight * heightRatio),
								new Vector2(x, TimelineScrubberHeight), LightGrey);
						}

						// Draw label
						if (lodWidthList[idx] >= ScrubberIntervalToShowLabel) {
							var seconds = i * curUnitSecond;
							GUI.Label(new Rect(x + 4.0f, -2, 50, 15), $"{(int)seconds:0}:{seconds % 1.0f * 100.0f:00}",
								EditorStyles.miniLabel);
						}

						break;
					}

					--idx;
				}
			}

			GUI.EndGroup();

			// Scrubber events
			if (scrubberRect.Contains(e.mousePosition))
				if (e.type == EventType.MouseDown)
					if (e.button == 0) {
						_dragState = DragState.SCRUB;
						_animTime = GuiPosToAnimTime(scrubberRect, e.mousePosition.x);
						GUI.FocusControl(None);
						e.Use();
					}

			if (_dragState == DragState.SCRUB && e.button == 0) {
				if (e.type == EventType.MouseDrag) {
					_animTime = GuiPosToAnimTime(scrubberRect, e.mousePosition.x);
					e.Use();
				}
				else if (e.type == EventType.MouseUp) {
					_dragState = DragState.NONE;
					e.Use();
				}
			}

			elementPosY += elementHeight;

			// Draw frames
			elementHeight = timelineRect.height - (elementPosY - timelineRect.yMin + TimelineBottomBarHeight);
			var rectFrames = new Rect(timelineRect) { yMin = elementPosY, height = elementHeight };

			LayoutFrames(rectFrames);

			elementPosY += elementHeight;

			// Draw playhead
			var playheadRect = new Rect(timelineRect) { height = timelineRect.height - TimelineBottomBarHeight };

			var offset = playheadRect.xMin + _timelineOffset + _animTime * _timelineScale;
			DrawLine(new Vector2(offset, playheadRect.yMin), new Vector2(offset, playheadRect.yMax), Magenta);

			// Draw bottom
			elementHeight = TimelineBottomBarHeight;
			var bottomBarRect = new Rect(timelineRect) { yMin = elementPosY, height = elementHeight };
			// Set min width
			bottomBarRect = new Rect(bottomBarRect) { width = Mathf.Max(bottomBarRect.width, 655) };
			GUI.BeginGroup(bottomBarRect, GUIElements.TimelineBottomBarBg);

			// Offset internal content
			bottomBarRect = new Rect(bottomBarRect) { height = bottomBarRect.height - 6, y = bottomBarRect.y + 3 };
			GUI.BeginGroup(new Rect(bottomBarRect) { y = 3 });

			if (_selectedFrames.Count == 1) {
				// Animation Frame data editor
				var frame = _selectedFrames[0];

				EditorGUI.BeginChangeCheck();

				float xOffset = 10;
				float width = 60;
				GUI.Label(new Rect(xOffset, 1, width, bottomBarRect.height), "Frame:", EditorStyles.boldLabel);

				// Function Name
				xOffset += width;
				width = 250;

				frame.sprite = EditorGUI.ObjectField(new Rect(xOffset, 2, width,
					bottomBarRect.height - 3), frame.sprite, typeof(Sprite), false) as Sprite;

				xOffset += width + 5;
				width = 50;

				// Frame length (in samples)
				EditorGUI.LabelField(new Rect(xOffset, 2, width, bottomBarRect.height - 3), "Length");

				xOffset += width + 5;
				width = 30;

				GUI.SetNextControlName("FrameLen");
				var frameLen = Mathf.RoundToInt(frame.length / (1.0f / clip.frameRate));
				frameLen = EditorGUI.IntField(new Rect(xOffset, 2, width, bottomBarRect.height - 3), frameLen);
				SetFrameLength(frame, frameLen * (1.0f / clip.frameRate));

				if (EditorGUI.EndChangeCheck())
					// Apply events
					ApplyChanges();
			}

			GUI.EndGroup();
			GUI.EndGroup();

			// Draw Frame Reposition
			LayoutMoveFrame(rectFrames);

			// Draw Insert
			if (e.type is EventType.DragUpdated or EventType.DragPerform && rectFrames.Contains(e.mousePosition))
				if (Array.Exists(DragAndDrop.objectReferences, item => item is Sprite or Texture2D)) {
					int closestFrame;
					if (e.control) {
						// When CTRL is held, frames are replaced rather than inserted
						DragAndDrop.visualMode = DragAndDropVisualMode.Move;
						closestFrame = MousePosToReplaceFrameIndex(rectFrames);
						LayoutReplaceFramesBox(rectFrames, closestFrame,
							DragAndDrop.objectReferences.Length); // Holding CTRL, show frames that'll be replaced
					}
					else {
						DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
						closestFrame = MousePosToInsertFrameIndex(rectFrames);
						LayoutInsertFramesLine(rectFrames, closestFrame);
					}

					_dragDropHovering = true;

					if (e.type == EventType.DragPerform) {
						DragAndDrop.AcceptDrag();
						var sprites = new List<Sprite>();
						for (var i = 0; i < DragAndDrop.objectReferences.Length; i++) {
							var obj = DragAndDrop.objectReferences[i];
							if (obj is Sprite sprite) {
								sprites.Add(sprite);
							}
							else if (obj is Texture2D) {
								// Grab all sprites associated with a texture, add to list
								var path = AssetDatabase.GetAssetPath(obj);
								var assets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
								for (var j = 0; j < assets.Length; j++) {
									var subAsset = assets[j];
									if (subAsset is Sprite asset)
										sprites.Add(asset);
								}
							}
						}

						// Sort sprites by name and insert
						using (var comparer = new NaturalComparer()) {
							sprites.Sort((a, b) => comparer.Compare(a.name, b.name));
						}

						// CTRL is held, frames are replaced
						if (e.control) {
							// Replaces sprites starting at a specific frame position. If there are more sprites than
							// existing frames more are created.
							var spritesArray = sprites.ToArray();
							// If there are no frames or replacing after last frame, do a normal insert.
							if (frames is null || frames.Count <= closestFrame) {
								InsertFrames(spritesArray, closestFrame);
								return;
							}

							List<Sprite> extraSpritesToInsert = null;
							for (var i = 0; i < spritesArray.Length; i++)
								if (i < frames.Count - closestFrame) {
									// If amount of dragged sprites fit the current list
									frames[i + closestFrame].sprite = spritesArray[i];
								}
								else {
									extraSpritesToInsert ??=
										new List<Sprite>(spritesArray.Length - (frames.Count - closestFrame));
									extraSpritesToInsert.Add(spritesArray[i]);
								}

							if (extraSpritesToInsert != null) {
								// If there are too many sprites to fit the current list, insert the extra ones at the end
								InsertFrames(extraSpritesToInsert.ToArray(), frames.Count);
							}
							else {
								RecalcFrameTimes();
								Repaint();
								ApplyChanges();
							}
						}
						else {
							InsertFrames(sprites.ToArray(), closestFrame);
						}
					}
				}

			// The indicator won't update while drag & dropping because it's not active, hack using this flag
			if (_dragDropHovering && rectFrames.Contains(e.mousePosition)) {
				if (e.control)
					LayoutReplaceFramesBox(rectFrames, MousePosToReplaceFrameIndex(rectFrames),
						DragAndDrop.objectReferences.Length); // Holding CTRL show frames that'll be replaced
				else LayoutInsertFramesLine(rectFrames, MousePosToInsertFrameIndex(rectFrames));
			}
			else {
				_dragDropHovering = false;
			}

			if (e.type == EventType.DragExited) _dragDropHovering = false;

			// Handle events
			if (timelineRect.Contains(e.mousePosition)) {
				if (e.type == EventType.ScrollWheel) {
					var scale = 8000.0f;
					while (_timelineScale / scale < 1.0f || _timelineScale / scale > 10.0f) scale /= 10.0f;

					var oldCursorTime = GuiPosToAnimTime(timelineRect, e.mousePosition.x);

					_timelineScale -= e.delta.y * scale * 0.05f;
					_timelineScale = Mathf.Clamp(_timelineScale, 40.0f, 8000.0f);

					// Offset to time at old cursor pos is same as at new position (so can zoom in/out of current cursor pos)
					_timelineOffset += e.mousePosition.x -
					                   (timelineRect.xMin + _timelineOffset + oldCursorTime * _timelineScale);

					Repaint();
					e.Use();
				}
				else if (e.type == EventType.MouseDrag) {
					if (e.button is 1 or 2) {
						_timelineOffset += e.delta.x;
						Repaint();
						e.Use();
					}
				}
			}

			if (e.rawType == EventType.MouseUp && e.button == 0 && _dragState == DragState.SELECT_FRAME) {
				_dragState = DragState.NONE;
				Repaint();
			}

			// Handle keypress events that are also used in text fields, this requires check that a text box doesn't have focus
			if (focusedWindow == this) {
				var allowKeypress = string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()) ||
				                    GUI.GetNameOfFocusedControl() == None;
				if (allowKeypress && e.type == EventType.KeyDown)
					switch (e.keyCode) {
						case KeyCode.Space: //Toggle Playback
						{
							playing = !playing;

							// Set auto play var. Anims will auto play when selected unless user has manually stopped an anim.
							autoPlay = playing;

							if (playing)
								// Clicked play: If anim is at end, restart
								if (_animTime >= GetAnimLength())
									_animTime = 0;
							e.Use();
						}
							break;
						case KeyCode.LeftArrow:
						case KeyCode.RightArrow: {
							int index;
							// Change selected frame (if only 1 frame is selected)
							if (_selectedFrames.Count > 0) {
								// Find index of frame before selected frames (if left arrow) or after selected frames (if right arrow)
								if (e.keyCode == KeyCode.LeftArrow)
									index = frames.FindIndex(frame => frame == _selectedFrames[0]) - 1;
								else
									index = frames.FindLastIndex(frame => frame == _selectedFrames[^1]) + 1;
							}
							else {
								index = GetCurrentFrameID() + (e.keyCode == KeyCode.LeftArrow ? -1 : 1);
							}

							index = Mathf.Clamp(index, 0, frames.Count - 1);

							SelectFrame(frames[index]);

							e.Use();
							Repaint();
						}
							break;
					}
			}

			// Handle event commands: Delete, SelectAll, Duplicate, Copy, Paste
			if (e.type == EventType.ValidateCommand)
				switch (e.commandName) {
					case "Delete":
					case "SoftDelete":
					case "SelectAll":
					case "Duplicate":
					case "Copy":
					case "Paste": {
						e.Use();
					}
						break;
				}

			if (e.type == EventType.ExecuteCommand)
				switch (e.commandName) {
					case "Delete":
					case "SoftDelete": {
						// Delete all selected frames
						if (_selectedFrames.Count > 0) {
							frames.RemoveAll(item => _selectedFrames.Contains(item));
							RecalcFrameTimes();
						}

						_selectedFrames.Clear();
						Repaint();
						ApplyChanges();
						e.Use();
					}
						break;

					case "SelectAll": {
						if (frames.Count > 0) {
							_selectedFrames.Clear();
							_selectedFrames.AddRange(frames);
						}


						e.Use();
					}
						break;

					case "Duplicate": {
						if (frames.Count == 0 || _selectedFrames.Count == 0) return;

						var lastSelected = _selectedFrames[^1];
						var index = frames.FindLastIndex(item => item == lastSelected) + 1;

						// Clone all items
						var duplicatedItems = _selectedFrames.ConvertAll(Clone);

						// Add duplicated frames
						frames.InsertRange(index, duplicatedItems);

						// Select the newly created frames
						_selectedFrames.Clear();
						_selectedFrames = duplicatedItems;

						RecalcFrameTimes();
						Repaint();
						ApplyChanges();

						e.Use();
					}
						break;

					case "Copy": {
						copiedFrames = null;
						if (_selectedFrames.Count == 0) return;
						copiedFrames = _selectedFrames.ConvertAll(Clone);
						e.Use();
					}
						break;

					case "Paste": {
						if (copiedFrames is { Count: > 0 }) {
							// Find place to insert, either after selected frame, at caret, or at end of anim
							frames ??= new List<AnimFrame>();
							var index = frames.Count;
							if (_selectedFrames.Count > 0) {
								// If there's a selected item, then insert after it
								var lastSelected = _selectedFrames[^1];
								index = frames.FindLastIndex(item => item == lastSelected) + 1;
							}
							else if (playing == false) {
								index = GetCurrentFrameID();
							}

							var pastedItems = copiedFrames.ConvertAll(Clone);
							index = Mathf.Clamp(index, 0, frames.Count);
							frames.InsertRange(index, pastedItems);
							_selectedFrames.Clear();
							_selectedFrames = pastedItems;

							RecalcFrameTimes();
						}

						Repaint();
						ApplyChanges();
						e.Use();
					}
						break;
				}
		}

		private void LayoutFrameSprite(Rect rect, Sprite sprite, float scale, Vector2 offset, bool useTextureRect,
			bool clipToRect, float angle = 0) {
			if (rect.width <= 10 || rect.height <= 10) return;

#if (UNITY_2017_1_OR_NEWER && !UNITY_2017_4_OR_NEWER) || (UNITY_2019_1_OR_NEWER && !UNITY_2019_3_OR_NEWER)
			LayoutFrameSpriteTexture(rect,sprite,scale,offset,useTextureRect,clipToRect, angle);
#else
			LayoutFrameSpriteRendered(rect, sprite, scale, offset, useTextureRect, angle);
#endif
		}

		// This layout just draws the sprite using gui tools - PreviewRenderUtility is broken in Unity 2017 so this is necessary
		private void LayoutFrameSpriteTexture(Rect rect, Sprite sprite, float scale, Vector2 offset,
			bool useTextureRect,
			bool clipToRect, float angle = 0) {
#if (UNITY_2017_1_OR_NEWER && !UNITY_2017_4_OR_NEWER) || (UNITY_2019_1_OR_NEWER && !UNITY_2019_3_OR_NEWER)
		if ( Application.isPlaying && (UnityEditor.EditorSettings.spritePackerMode == SpritePackerMode.AlwaysOn || UnityEditor.EditorSettings.spritePackerMode == SpritePackerMode.AlwaysOnAtlas) && sprite.packed && sprite.packingMode != SpritePackingMode.Rectangle )
			return; //	useTextureRect = false; // When playing, the sprite shows a meaningless section of the atlas, so just return immediately
#endif
			// Calculate pivot offset
			var pivotOffset = Vector2.zero;

			if (useTextureRect == false) {
				pivotOffset = (sprite.rect.size * 0.5f - sprite.pivot) * scale;
				pivotOffset.y = -pivotOffset.y;
			}

			var spriteRectOriginal = useTextureRect ? sprite.textureRect : sprite.rect;
			var texCoords = new Rect(spriteRectOriginal.x / sprite.texture.width,
				spriteRectOriginal.y / sprite.texture.height, spriteRectOriginal.width / sprite.texture.width,
				spriteRectOriginal.height / sprite.texture.height);

			var spriteRect = new Rect(Vector2.zero, spriteRectOriginal.size * scale) {
				center = rect.center + offset + pivotOffset
			};

			if (clipToRect) {
				// If the sprite doesn't fit in the rect, it needs to be cropped, and have it's uv's scaled to compensate
				var croppedRectOffset = new Vector2(Mathf.Max(spriteRect.xMin, rect.xMin),
					Mathf.Max(spriteRect.yMin, rect.yMin));
				var croppedRectSize =
					new Vector2(Mathf.Min(spriteRect.xMax, rect.xMax), Mathf.Min(spriteRect.yMax, rect.yMax)) -
					croppedRectOffset;
				var croppedRect = new Rect(croppedRectOffset, croppedRectSize);
				texCoords.x += (croppedRect.xMin - spriteRect.xMin) / spriteRect.width * texCoords.width;
				texCoords.y += (spriteRect.yMax - croppedRect.yMax) / spriteRect.height * texCoords.height;
				texCoords.width *= 1.0f - (spriteRect.width - croppedRect.width) / spriteRect.width;
				texCoords.height *= 1.0f - (spriteRect.height - croppedRect.height) / spriteRect.height;

				GUI.DrawTextureWithTexCoords(croppedRect, sprite.texture, texCoords, true);
			}
			else {
				GUI.DrawTextureWithTexCoords(spriteRect, sprite.texture, texCoords, true);
			}
		}

		// This renders the sprite polygon with a camera. More expensive but works with atlases/polygon sprites
		private void LayoutFrameSpriteRendered(Rect rect, Sprite sprite, float scale, Vector2 offset,
			bool useTextureRect, float angle = 0) {
			while (true) {
				Camera previewCamera;

				if (_prevRender is null) {
					_prevRender = new PreviewRenderUtility();
					previewCamera = _prevRender.camera;
					previewCamera.orthographic = true;
					previewCamera.transform.rotation = Quaternion.identity;
					previewCamera.nearClipPlane = 1;
					previewCamera.farClipPlane = 30;
					previewCamera.backgroundColor = Clear;
				}

				if (_spriteRenderData.TryGetValue(sprite, out var data) == false) {
					if (_defaultSpriteShader is null) _defaultSpriteShader = Shader.Find("Sprites/Default");

					// First time this sprite has been encountered. Instantiate the render data for it and cache it
					data = new SpriteRenderData {
						mat = new Material(_defaultSpriteShader),
						previewMesh = new Mesh()
					};

					var newMesh = new Vector3[sprite.vertices.Length];
					for (var i = 0; i < newMesh.Length; ++i) newMesh[i] = sprite.vertices[i];
					var newTris = new int[sprite.triangles.Length];
					for (var i = 0; i < newTris.Length; ++i) newTris[i] = sprite.triangles[i];

					data.mat.mainTexture = sprite.texture;
					data.previewMesh.vertices = newMesh;
					data.previewMesh.uv = sprite.uv;
					data.previewMesh.triangles = newTris;
					data.previewMesh.RecalculateBounds();
					data.previewMesh.RecalculateNormals();

					_spriteRenderData.Add(sprite, data);
				}

				if (data.mat is null || data.previewMesh is null) {
					_spriteRenderData.Clear();
					angle = 0;
					continue;
				}

				// Setup preview camera size & pos
				var finalScaleInv = 1.0f / (scale * sprite.pixelsPerUnit);

				previewCamera = _prevRender.camera;

				previewCamera.orthographicSize = 0.5f * rect.height * finalScaleInv;
				previewCamera.transform.position =
					new Vector3(-offset.x * finalScaleInv, offset.y * finalScaleInv, -10f);

				// Begin Preview
				_prevRender.BeginPreview(rect, GUIStyle.none);

				// Offset from pivot so that sprite is centered correctly
				var pivotOffset = Vector2.zero;

				// If using the texture rect (eg. in timeline) - remove pivot offset
				if (useTextureRect) pivotOffset = -(sprite.rect.size * 0.5f - sprite.pivot);

				// If using the texture rect (eg. in timeline)- Remove difference between centerpoint of sprite rect and texture rect (can't do it if playing with tight packing though)
				if (useTextureRect && (sprite.packed == false || Application.isPlaying == false))
					pivotOffset += sprite.rect.center - sprite.textureRect.center;

				// Draw the mesh
				_prevRender.DrawMesh(data.previewMesh, pivotOffset / sprite.pixelsPerUnit,
					Quaternion.Euler(0, 0, angle), data.mat, 0);

				// Render preview to texture
				previewCamera.Render();
				var texture = _prevRender.EndPreview();
				texture.filterMode = FilterMode.Point;

				// Draw on the GUI
				GUI.DrawTexture(rect, texture);
				break;
			}
		}

		private void LayoutFrames(Rect rect) {
			var e = Event.current;

			GUI.BeginGroup(rect, GUIElements.TimelineAnimBg);

			for (var i = 0; i < frames.Count; ++i) // Ignore final dummy keyframe
				// Calculate time of next frame
				LayoutFrame(rect, i, frames[i].time, frames[i].EndTime);

			// Draw rect over area that has no frames in it
			if (_timelineOffset > 0) {
				// Before frames start
				EditorGUI.DrawRect(new Rect(0, 0, _timelineOffset, rect.height), GreyA20);
				DrawLine(new Vector2(_timelineOffset, 0), new Vector2(_timelineOffset, rect.height), GreyA40);
			}

			var endOffset = _timelineOffset + GetAnimLength() * _timelineScale;
			if (endOffset < rect.xMax)
				// After frames end
				EditorGUI.DrawRect(new Rect(endOffset, 0, rect.width - endOffset, rect.height), GreyA20);

			GUI.EndGroup();

			// Draw selection rect
			if (_dragState == DragState.SELECT_FRAME && Mathf.Abs(_selectionMouseStart - e.mousePosition.x) > 1.0f) {
				// Draw selection rect
				var selectionRect = new Rect(rect) {
					xMin = Mathf.Min(_selectionMouseStart, e.mousePosition.x),
					xMax = Mathf.Max(_selectionMouseStart, e.mousePosition.x)
				};

				// Draw Background
				EditorGUI.DrawRect(selectionRect, BlueA10);

				// Draw border
				selectionRect.width -= 1;
				selectionRect.height -= 1;
				DrawLine(new Vector2(selectionRect.xMin, selectionRect.yMin),
					new Vector2(selectionRect.xMin, selectionRect.yMax), BlueA60, 1);
				DrawLine(new Vector2(selectionRect.xMin, selectionRect.yMax),
					new Vector2(selectionRect.xMax, selectionRect.yMax), BlueA60, 1);
				DrawLine(new Vector2(selectionRect.xMax, selectionRect.yMax),
					new Vector2(selectionRect.xMax, selectionRect.yMin), BlueA60, 1);
				DrawLine(new Vector2(selectionRect.xMax, selectionRect.yMin),
					new Vector2(selectionRect.xMin, selectionRect.yMin), BlueA60, 1);
			}

			if (_dragState == DragState.NONE) {
				// Deselect any selected frames on left mouse click
				if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition)) {
					_selectedFrames.Clear();
					e.Use();
				}

				// Check for unhandled drag. Start a select
				if (e.type != EventType.MouseDrag || e.button != 0 || !rect.Contains(e.mousePosition)) return;
				_dragState = DragState.SELECT_FRAME;
				e.Use();
			}
			else if (_dragState == DragState.RESIZE_FRAME) {
				// When resizing frame show the resize cursor
				EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
			}
			else if (_dragState == DragState.MOVE_FRAME) {
				// When moving frame show the move cursor
				EditorGUIUtility.AddCursorRect(rect, MouseCursor.MoveArrow);
			}
		}

		private void LayoutFrame(Rect rect, int frameId, float startTime, float endTime) {
			var startOffset = _timelineOffset + startTime * _timelineScale;
			var endOffset = _timelineOffset + endTime * _timelineScale;

			// Check if visible on timeline
			if (startOffset > rect.xMax || endOffset < rect.xMin) return;
			var animFrame = frames[frameId];
			var frameRect = new Rect(startOffset, 0, endOffset - startOffset, rect.height);
			var selected = _selectedFrames.Contains(animFrame);
			if (selected)
				// Highlight selected frames
				EditorGUI.DrawRect(frameRect, BlueA10);

			DrawLine(new Vector2(endOffset, 0), new Vector2(endOffset, rect.height), GreyA40);
			
			// Layout Timeline Sprite
			var sprite = GetFrameAtTime(startTime).sprite;
			if (sprite is not null) {
				var scale = 0.85f;
				var timelineSpriteRect = sprite.packed && Application.isPlaying ? sprite.rect : sprite.textureRect;
				if (timelineSpriteRect.width > 0 && timelineSpriteRect.height > 0) {
					var widthScaled = frameRect.width / timelineSpriteRect.width;
					var heightScaled = frameRect.height / timelineSpriteRect.height;
					if (widthScaled < heightScaled) scale *= frameRect.width / timelineSpriteRect.width;
					else scale *= frameRect.height / timelineSpriteRect.height;
				}

				LayoutFrameSprite(frameRect, sprite, scale, Vector2.zero, true, false);
			}

			// Frame clicking events
			var e = Event.current;

			if (_dragState == DragState.NONE) {
				// Move cursor (when selected, it can be dragged to move it)
				if (selected)
					EditorGUIUtility.AddCursorRect(
						new Rect(frameRect) {
							xMin = frameRect.xMin + FrameResizeRectWidth * 0.5f,
							xMax = frameRect.xMax - FrameResizeRectWidth * 0.5f
						}, MouseCursor.MoveArrow);

				// Resize rect
				var resizeRect = new Rect(endOffset - FrameResizeRectWidth * 0.5f, 0, FrameResizeRectWidth,
					rect.height);
				EditorGUIUtility.AddCursorRect(resizeRect, MouseCursor.ResizeHorizontal);

				// Check for Start Resizing frame
				if (e.type == EventType.MouseDown && e.button == 0 && resizeRect.Contains(e.mousePosition)) {
					// Start resizing the frame
					_dragState = DragState.RESIZE_FRAME;
					_resizeFrameId = frameId;
					GUI.FocusControl(None);
					e.Use();
				}

				// Handle Frame Selection
				if (selected == false && e.type == EventType.MouseDown && e.button == 0 &&
				    frameRect.Contains(e.mousePosition)) {
					// Started clicking unselected - start selecting
					_dragState = DragState.SELECT_FRAME;
					SelectFrame(animFrame);
					GUI.FocusControl(None);
					e.Use();
				}

				if (selected && _selectedFrames.Count > 1 && e.type == EventType.MouseUp && e.button == 0 &&
				    frameRect.Contains(e.mousePosition)) {
					// Had multiple selected, and clicked on just one, deselect others
					SelectFrame(animFrame);
					e.Use();
				}

				// Handle start move frame drag (once selected)
				if (selected && e.type == EventType.MouseDrag && e.button == 0 && frameRect.Contains(e.mousePosition)) {
					_dragState = DragState.MOVE_FRAME;
					e.Use();
				}

				if (selected && e.type == EventType.MouseDown && e.button == 0 && frameRect.Contains(e.mousePosition)) {
					// Clicked already selected item
					GUI.FocusControl(None);
					// Consume event so it doesn't get deselected when starting a move
					e.Use();
				}
			}
			else if (_dragState == DragState.RESIZE_FRAME) {
				// Check for resize frame by dragging mouse
				if (e.type == EventType.MouseDrag && e.button == 0 && _resizeFrameId == frameId) {
					var minFrameTime = 1.0f / clip.frameRate;

					if (selected && _selectedFrames.Count > 1) {
						// Calculate frame end if adding a frame to each selected frame.
						var currEndTime = animFrame.time + animFrame.length;
						var newEndTime = animFrame.time + animFrame.length;
						var mouseTime = GuiPosToAnimTime(new Rect(0, 0, position.width, position.height),
							e.mousePosition.x);
						var direction = Mathf.Sign(mouseTime - currEndTime);

						for (var i = 0; i < _selectedFrames.Count; ++i)
							if (_selectedFrames[i].time <= animFrame.time || i == frameId)
								newEndTime += minFrameTime * direction;

						// If mouse time is closer to newEndTime than currEndTime then commit the change
						if (Mathf.Abs(mouseTime - newEndTime) < Mathf.Abs(mouseTime - currEndTime)) {
							for (var i = 0; i < _selectedFrames.Count; i++) {
								var frame = _selectedFrames[i];
								var time = frame.length + minFrameTime * direction;
								time = Mathf.Round(time * clip.frameRate) / clip.frameRate;
								frame.length = Mathf.Max(minFrameTime, time);
							}

							RecalcFrameTimes();
						}
					}
					else {
						var newFrameLength =
							GuiPosToAnimTime(new Rect(0, 0, position.width, position.height), e.mousePosition.x) -
							startTime;
						newFrameLength = Mathf.Max(newFrameLength, minFrameTime);
						//Set Frame Length
						if (Mathf.Approximately(frameId, frames[frameId].length) == false) {
							frames[frameId].length = Mathf.Max(minFrameTime,
								Mathf.Round(newFrameLength * clip.frameRate) / clip.frameRate);
							RecalcFrameTimes();
						}
					}

					e.Use();
					Repaint();
				}

				// Check for finish resizing frame
				if (e.type == EventType.MouseUp && e.button == 0 && _resizeFrameId == frameId) {
					_dragState = DragState.NONE;
					ApplyChanges();
					e.Use();
				}
			}
			else if (_dragState == DragState.SELECT_FRAME) {
				if (e.type == EventType.MouseUp && e.button == 0) {
					_dragState = DragState.NONE;
					e.Use();
				}
			}
		}
		
		// Handles moving frames
		private void LayoutMoveFrame(Rect rect) {
			var e = Event.current;

			if (_dragState == DragState.MOVE_FRAME) {
				var closestFrame = MousePosToInsertFrameIndex(rect);

				LayoutInsertFramesLine(rect, closestFrame);

				if (e.type == EventType.MouseDrag && e.button == 0) e.Use();

				if (e.type == EventType.MouseUp && e.button == 0) {
					// Move selected frame to before closestFrame
					// Sort selected items by time so they can be moved in correct order
					_selectedFrames.Sort((a, b) => a.time.CompareTo(b.time));

					var insertAtEnd = closestFrame >= frames.Count;

					// Insert all items (remove from list, and re-add in correct position.
					for (var i = 0; i < _selectedFrames.Count; i++) {
						var frame = _selectedFrames[i];
						if (insertAtEnd) {
							if (frames[^1] != frame) {
								frames.Remove(frame);
								frames.Add(frame);
							}
						}
						else {
							var insertBeforeFrame = frames[closestFrame];
							if (insertBeforeFrame != frame) {
								frames.Remove(frame);
								closestFrame = frames.FindIndex(item => item == insertBeforeFrame);
								frames.Insert(closestFrame, frame);
							}

							closestFrame++;
						}
					}

					RecalcFrameTimes();
					Repaint();
					ApplyChanges();

					_dragState = DragState.NONE;
					e.Use();
				}
			}
		}

		// Draws line that shows where frames will be inserted
		private void LayoutInsertFramesLine(Rect rect, int frameId) {
			var time = frameId < frames.Count ? frames[frameId].time : GetAnimLength();
			var posOnTimeline = _timelineOffset + time * _timelineScale;

			// Check if visible on timeline
			if (posOnTimeline < rect.xMin || posOnTimeline > rect.xMax) return;
			DrawLine(new Vector2(posOnTimeline, rect.yMin), new Vector2(posOnTimeline, rect.yMax), Blue);
		}

		private void LayoutReplaceFramesBox(Rect rect, int frameId, int numFrames) {
			var time = frameId < frames.Count ? frames[frameId].time : GetAnimLength();
			var startPosOnTimeline = _timelineOffset + time * _timelineScale;
			var finalTimeId = frameId + numFrames;
			var finalTime = finalTimeId < frames.Count ? frames[finalTimeId].time : GetAnimLength() + 0.0f;
			var endPosOnTimeline = _timelineOffset + finalTime * _timelineScale;

			var selectionRect = new Rect(rect) {
				xMin = Mathf.Max(rect.xMin, startPosOnTimeline), xMax = Mathf.Min(rect.xMax, endPosOnTimeline)
			};

			// Draw Background
			EditorGUI.DrawRect(selectionRect, BlueA10);

			// Draw border
			selectionRect.width -= 1;
			selectionRect.height -= 1;
			DrawLine(new Vector2(selectionRect.xMin, selectionRect.yMin),
				new Vector2(selectionRect.xMin, selectionRect.yMax), BlueA60, 1);
			DrawLine(new Vector2(selectionRect.xMin, selectionRect.yMax),
				new Vector2(selectionRect.xMax, selectionRect.yMax), BlueA60, 1);
			DrawLine(new Vector2(selectionRect.xMax, selectionRect.yMax),
				new Vector2(selectionRect.xMax, selectionRect.yMin), BlueA60, 1);
			DrawLine(new Vector2(selectionRect.xMax, selectionRect.yMin),
				new Vector2(selectionRect.xMin, selectionRect.yMin), BlueA60, 1);
		}

		private float GuiPosToAnimTime(Rect rect, float mousePosX) {
			var pos = mousePosX - rect.xMin;
			return (pos - _timelineOffset) / _timelineScale;
		}

		// Returns the point- Set to frames.Length if should insert after final frame
		private int MousePosToInsertFrameIndex(Rect rect) {
			if (frames.Count == 0) return 0;

			// Find point between two frames closest to mouse cursor so we can show indicator
			var closest = float.MaxValue;
			var animTime = GuiPosToAnimTime(rect, Event.current.mousePosition.x);
			var closestFrame = 0;
			for (; closestFrame < frames.Count + 1; ++closestFrame) {
				// Loop through frames until find one that's further away than the last from the mouse pos
				// For final iteration it checks the end time of the last frame rather than start time
				var frameStartTime = closestFrame < frames.Count
					? frames[closestFrame].time
					: frames[closestFrame - 1].EndTime;
				var diff = Mathf.Abs(frameStartTime - animTime);
				if (diff > closest) break;
				closest = diff;
			}

			closestFrame = Mathf.Clamp(closestFrame - 1, 0, frames.Count);
			return closestFrame;
		}

		// Returns frame that mouse is hovering over
		private int MousePosToReplaceFrameIndex(Rect rect) {
			if (frames.Count == 0) return 0;

			// Find point between two frames closest to mouse cursor so indicator can be shown
			var animTime = GuiPosToAnimTime(rect, Event.current.mousePosition.x);
			var closestFrame = 0;
			while (closestFrame < frames.Count && frames[closestFrame].EndTime <= animTime) ++closestFrame;
			closestFrame = Mathf.Clamp(closestFrame, 0, frames.Count);
			return closestFrame;
		}

		private void Update() {
			if (clip != null && playing && _dragState != DragState.SCRUB) {
				// Update anim time if playing (and not scrubbing)
				var delta = (float)(EditorApplication.timeSinceStartup - _editorTimePrev);

				_animTime += delta * _previewSpeedScale;

				if (_animTime >= GetAnimLength()) {
					if (previewLoop) {
						_animTime -= GetAnimLength();
					}
					else {
						playing = false;
						_animTime = 0;
					}
				}

				Repaint();
			}
			else if (_dragDropHovering || _dragState != DragState.NONE) {
				Repaint();
			}

			// When going to Play, we need to clear the selection since references get broken.
			if (_wasPlaying != EditorApplication.isPlayingOrWillChangePlaymode) {
				_wasPlaying = EditorApplication.isPlayingOrWillChangePlaymode;
				if (_wasPlaying) _selectedFrames.Clear();
			}

			_editorTimePrev = EditorApplication.timeSinceStartup;
		}

		private void OnClipChange(bool resetPreview = true) {
			if (clip == null) return;
			frames = null;

			// Find curve binding for the sprite. This property is for sprite anims
			_curveBinding = Array.Find(AnimationUtility.GetObjectReferenceCurveBindings(clip),
				item => item.propertyName == PropertyNameSprite);
			if (_curveBinding.isPPtrCurve) {
				// Convert frames from ObjectReferenceKeyframe (struct with time & sprite) list of AnimFrame
				var objRefKeyframes = AnimationUtility.GetObjectReferenceCurve(clip, _curveBinding);
				frames = new List<AnimFrame>(Array.ConvertAll(objRefKeyframes,
					keyframe => new AnimFrame { time = keyframe.time, sprite = keyframe.value as Sprite }));
			}

			frames ??= new List<AnimFrame>();

			// Update the lengths of each frame based on the times
			for (var i = 0; i < frames.Count - 1; ++i) frames[i].length = frames[i + 1].time - frames[i].time;

			var minFrameTimeLength = 1.0f / clip.frameRate;
			// If last frame has invalid length, set it to minimum length
			if (frames.Count > 0 && frames[^1].length < minFrameTimeLength) frames[^1].length = minFrameTimeLength;

			// Hack/Unhack the final frame. To get around unity limitation of final frame always being 1 sample long
			// a dummy duplicate frame is added to the end
			var numFrames = frames.Count;
			if (numFrames >= 2) {
				var lastFrame = frames[numFrames - 1];
				var secondLastFrame = frames[numFrames - 2];
				if (lastFrame.sprite == secondLastFrame.sprite) {
					// Last frame was a duplicate, so just increase the length of the second last frame and remove the dummy one
					secondLastFrame.length += minFrameTimeLength;
					frames.RemoveAt(numFrames - 1);
				}

				else {
					lastFrame.length = minFrameTimeLength;
				}
			}

			// Update other internal data.
			_framesReorderableList.list = frames;
			if (resetPreview) {
				_previewResetScale = true;
				previewLoop = clip.isLooping;
				_animTime = 0;
				playing = autoPlay;
				_scrollPosition = Vector2.zero;
				_selectedFrames.Clear();
				_previewOffset = Vector2.zero;
				_timelineOffset = -TimelineOffsetMin;

				_spriteRenderData.Clear();
			}

			Repaint();

			// If num frames is 1 and that frame is empty, delete it, it's a new animation
			if (numFrames == 1)
				if (frames[0].sprite == null) {
					frames.RemoveAt(0);
					_selectedFrames.Clear();
					Repaint();
					ApplyChanges();
				}
		}

		/// Saves changes in the internal frames to the actual animation clip
		private void ApplyChanges() {
			var keyframes = frames
				.ConvertAll(item => new ObjectReferenceKeyframe { time = item.time, value = item.sprite })
				.ToArray();

			var hasFrames = keyframes.Length > 0;
			var hadFrames = _curveBinding.isPPtrCurve;

			// If final keyframe is > sample rate, there needs to be a duplicate keyframe added to the end
			if (hasFrames) {
				// Keyframes are stored as array of structs, with time the frame starts, and the sprite.
				// This means the last element will always be 1 sample long.
				// If final frame is larger than 1 sample long then add a duplicate
				var lastFrame = frames[^1];

				var minFrameTimeLength = 1.0f / clip.frameRate;
				if (lastFrame.length > minFrameTimeLength + 0.0001f) {
					// Add another frame
					Array.Resize(ref keyframes, keyframes.Length + 1);
					keyframes[^1] = new ObjectReferenceKeyframe {
						value = lastFrame.sprite, time = lastFrame.EndTime - minFrameTimeLength
					};
				}
			}

			Undo.RecordObject(clip, "Animation Change");

			if (hasFrames) {
				if (hadFrames == false)
					// Adding first frames, so need to create curve binding
					_curveBinding = new EditorCurveBinding {
						// Want to change the sprites of the sprite renderer
						type = typeof(SpriteRenderer), propertyName
							// Property change the sprite of a sprite renderer
							= PropertyNameSprite
					};

				// Apply the changes
				AnimationUtility.SetObjectReferenceCurve(clip, _curveBinding, keyframes);
			}
			else if (hadFrames) {
				// Had frames, but they've all been removed, so remove the curve binding
				AnimationUtility.SetObjectReferenceCurve(clip, _curveBinding, null);
			}
		}

		private void SetFrameLength(AnimFrame frame, float length) {
			if (Mathf.Approximately(length, frame.length) == false) {
				// Snaps a time to the closest sample time on the timeline
				frame.length = Mathf.Max(1.0f / clip.frameRate, Mathf.Round(length * clip.frameRate) / clip.frameRate);
				RecalcFrameTimes();
			}
		}

		/// Update the times of all frames based on the lengths
		private void RecalcFrameTimes() {
			float time = 0;
			for (var i = 0; i < frames.Count; i++) {
				var frame = frames[i];
				frame.time = time;
				time += frame.length;
			}
		}

		/// Add frames at a specific position
		private void InsertFrames(Sprite[] sprites, int atPos) {
			var frameLength = 1.0f / clip.frameRate;

			if (frames.Count > 0) {
				// Find previous frame's length to use for inserted frames
				frameLength = frames[atPos == 0 || atPos >= frames.Count ? 0 : atPos - 1].length;
			}
			else {
				// First frame, use default FPS
				if (defaultFrameLength > 0 && defaultFrameSamples > 0) {
					clip.frameRate = defaultFrameSamples / defaultFrameLength;
					frameLength = defaultFrameLength;
				}
			}

			var newFrames = Array.ConvertAll(sprites,
				sprite => new AnimFrame() { sprite = sprite, length = frameLength });

			atPos = Mathf.Clamp(atPos, 0, frames.Count);
			frames.InsertRange(atPos, newFrames);

			RecalcFrameTimes();
			Repaint();
			ApplyChanges();
		}

		/// Unity event called when the selected object changes
		private void OnSelectionChange() {
			var obj = Selection.activeObject;
			if (obj != clip && obj is AnimationClip) {
				clip = Selection.activeObject as AnimationClip;
				OnClipChange();
			}
		}

		/// Handles selection a single frame on timeline and list, and puts playhead at start
		private void SelectFrame(AnimFrame selectedFrame) {
			var ctrlClick = Event.current.control;
			var shiftClick =
				Event.current.shift && _selectedFrames.Count == 1; // Can only shift click if 1 is selected already

			// Clear existing events unless ctrl is clicked, or we're select dragging
			if (ctrlClick == false && shiftClick == false) _selectedFrames.Clear();

			// Don't add if already in selection list, and if holding ctrl remove it from the list
			if (_selectedFrames.Contains(selectedFrame) == false) {
				if (shiftClick) {
					// Add frames between selected and clicked.
					var indexFrom = frames.FindIndex(item => item == _selectedFrames[0]);
					var indexTo = frames.FindIndex(item => item == selectedFrame);
					if (indexFrom > indexTo)
						(indexFrom, indexTo) = (indexTo, indexFrom);

					for (var i = indexFrom + 1; i < indexTo; ++i) _selectedFrames.Add(frames[i]);
				}

				_selectedFrames.Add(selectedFrame);

				if (ctrlClick == false) {
					_framesReorderableList.index = frames.FindIndex(item => item == selectedFrame);

					// Put playhead at beginning of selected frame if not playing
					if (playing == false) _animTime = selectedFrame.time;
				}
			}
			else if (ctrlClick) {
				_selectedFrames.Remove(selectedFrame);
			}

			// Sort selection
			_selectedFrames.Sort((a, b) => a.time.CompareTo(b.time));
		}

		private float GetAnimLength() {
			if (frames is { Count: > 0 }) {
				var lastFrame = frames[^1];
				return lastFrame.time + lastFrame.length;
			}

			return 0;
		}

		private int GetCurrentFrameID() {
			if (frames == null || frames.Count == 0) return -1;
			var frame = frames.FindIndex(item => item.time > _animTime);
			if (frame < 0) frame = frames.Count;
			frame--;
			return frame;
		}

		private AnimFrame GetFrameAtTime(float time) {
			if (frames == null || frames.Count == 0) return null;
			var frame = frames.FindIndex(item => item.time > time);
			if (frame <= 0 || frame > frames.Count) frame = frames.Count;
			frame--;
			return frames[frame];
		}

		private static void DrawLine(Vector2 from, Vector2 to, Color color, float width = 0, bool snap = true) {
			if ((to - from).sqrMagnitude <= float.Epsilon) return;

			if (snap) {
				from.x = (int)from.x;
				from.y = (int)from.y;
				to.x = (int)to.x;
				to.y = (int)to.y;
			}

			var savedColor = Handles.color;
			Handles.color = color;

			if (width > 1.0f) Handles.DrawAAPolyLine(width, from, to);
			else Handles.DrawLine(from, to);

			Handles.color = savedColor;
		}

		private void OnUndoRedo() {
			OnClipChange(false);
		}

		private static T Clone<T>(T from) where T : new() {
			var result = new T();

			var finfos = from.GetType()
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			for (var i = 0; i < finfos.Length; i++) {
				var t = finfos[i];
				t.SetValue(result, t.GetValue(from));
			}

			return result;
		}

		/// Sort strings by natural order
		private class NaturalComparer : Comparer<string>, IDisposable {
			private Dictionary<string, string[]> _table;

			public NaturalComparer() {
				_table = new Dictionary<string, string[]>();
			}

			public void Dispose() {
				_table.Clear();
				_table = null;
			}

			public override int Compare(string x, string y) {
				if (x == y) return 0;

				if (!_table.TryGetValue(x, out var x1)) {
					x1 = Regex.Split(x.Replace(" ", ""), "([0-9]+)");
					_table.Add(x, x1);
				}

				if (!_table.TryGetValue(y, out var y1)) {
					y1 = Regex.Split(y.Replace(" ", ""), "([0-9]+)");
					_table.Add(y, y1);
				}

				for (var i = 0; i < x1.Length && i < y1.Length; i++)
					if (x1[i] != y1[i])
						return PartCompare(x1[i], y1[i]);

				if (y1.Length > x1.Length) return 1;
				if (x1.Length > y1.Length) return -1;

				return 0;
			}

			private static int PartCompare(string left, string right) {
				if (!int.TryParse(left, out var x)) return string.Compare(left, right, StringComparison.Ordinal);

				return !int.TryParse(right, out var y)
					? string.Compare(left, right, StringComparison.Ordinal)
					: x.CompareTo(y);
			}
		}

		private enum DragState {
			NONE,
			SCRUB,
			RESIZE_FRAME,
			MOVE_FRAME,
			SELECT_FRAME
		}
	}
}