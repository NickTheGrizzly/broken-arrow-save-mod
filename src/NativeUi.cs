using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using Il2CppBrokenArrow.Shared.Ecs.Localization;        // TextTarget
using Il2CppBrokenArrow.Client.Ecs.UI;                 // MainMenuScreen
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.Profile;    // EscapeMenu
using Il2CppBrokenArrow.Client.Ecs.UI.BaseElements;    // UiButton
using Il2CppBrokenArrow.Client.Ecs.UI.BaseElements.Popup; // PopupService, PopupCreationParams, UiPopupDialogue
using Il2CppBrokenArrow.Client.Ecs.Configs;            // UiConfig

namespace BASaveGame
{
    /// <summary>
    /// Native Save/Load UI, built from the game's own widgets so it looks and behaves like the rest
    /// of the menus (layout from the F3 dumps):
    ///  - Pause menu (EscapeMenu): "Save game" / "Load game" buttons cloned from Settings, placed
    ///    after "Restart mission". Clicking swaps the button list for a heading + slot list + Back.
    ///    Overwrite / load confirmations use the game's own dialog (UiPopupDialogue via PopupService,
    ///    prefab + colours from UiConfig), falling back to "click again" if that can't be shown.
    ///  - Main menu (MainMenuScreen, Home tab): a "Saved games" card cloned from the Campaign card
    ///    in the left column; it swaps the column for a list of saves, newest first.
    /// Cloned texts lose their TextTarget (localization) component, which would otherwise put the
    /// original label back. Polled from OnUpdate (no Harmony); each menu instance is decorated once.
    /// </summary>
    internal static class NativeUi
    {
        private const string Tag = "BASave ";
        private static float _nextCheck;

        internal static void Tick()
        {
            try { MainMenu.KeepPlaced(); } catch { }   // every frame, so a moved card never shows in the wrong spot
            float now = Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + 0.3f;
            try { PauseMenu.Tick(); } catch (Exception e) { Warn("pause menu: " + e.Message); }
            try { MainMenu.Tick(); } catch (Exception e) { Warn("main menu: " + e.Message); }
        }

        // =====================================================================================
        // Pause menu
        // =====================================================================================
        private static class PauseMenu
        {
            private enum Mode { Buttons, Save, Load }
            private static IntPtr _menu;
            private static EscapeMenu _escape;
            private static Transform _list;
            private static GameObject _template;            // our "Save game" button, reused as the row template
            private static Mode _mode = Mode.Buttons;
            private static readonly List<GameObject> _items = new List<GameObject>();
            private static readonly List<KeyValuePair<GameObject, bool>> _hidden = new List<KeyValuePair<GameObject, bool>>();
            private static int _armed = -1;                 // fallback confirmation when the dialog isn't available
            private static string _status;

            internal static void Tick()
            {
                EscapeMenu menu = null;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<EscapeMenu>()))
                {
                    var m = o.TryCast<EscapeMenu>();
                    if (m != null && m.gameObject.activeInHierarchy) { menu = m; break; }
                }
                if (menu == null)
                {
                    if (_mode != Mode.Buttons && _list != null) ShowButtons();   // menu closed while a list was open
                    return;
                }
                if (menu.Pointer == _menu && _list != null && _list.Find(Tag + "Save") != null) return;
                Decorate(menu);
            }

            private static void Decorate(EscapeMenu menu)
            {
                _menu = menu.Pointer;
                _escape = menu;
                _mode = Mode.Buttons;
                _items.Clear();
                _hidden.Clear();
                var settings = menu._settingsButton;
                if (settings == null) { Warn("pause menu has no settings button"); return; }
                _list = settings.transform.parent;
                int at = settings.transform.GetSiblingIndex();
                var restart = _list.Find("Button (restart mission)");
                if (restart != null) at = restart.GetSiblingIndex() + 1;

                var save = Clone(settings.gameObject, _list, Tag + "Save", "Save game", () => ShowSlots(Mode.Save));
                save.transform.SetSiblingIndex(at);
                var load = Clone(settings.gameObject, _list, Tag + "Load", "Load game", () => ShowSlots(Mode.Load));
                load.transform.SetSiblingIndex(at + 1);
                _template = save;
                Msg("pause menu: Save game / Load game added");
            }

            private static void ShowSlots(Mode mode)
            {
                if (_list == null) return;
                if (_mode == Mode.Buttons)
                {
                    _hidden.Clear();
                    for (int i = 0; i < _list.childCount; i++)
                    {
                        var go = _list.GetChild(i).gameObject;
                        _hidden.Add(new KeyValuePair<GameObject, bool>(go, go.activeSelf));
                        go.SetActive(false);
                    }
                }
                _mode = mode;
                _armed = -1;
                _status = null;
                Render();
            }

            private static void Render()
            {
                foreach (var go in _items) if (go != null) UnityEngine.Object.Destroy(go);
                _items.Clear();

                string heading = _status ?? (_mode == Mode.Save ? "SAVE GAME" : "LOAD GAME");
                var head = Label(_template, _list, Tag + "Heading", heading);
                SetHeight(head, 52f);
                _items.Add(head);

                if (_mode == Mode.Save)
                {
                    foreach (var s in SaveSlots.All().FindAll(x => x.Index > 0)) AddSlot(s);
                }
                else
                {
                    // Quicksave first (usually the newest), then the manual slots in slot order.
                    var quick = SaveSlots.Quick();
                    var manual = SaveSlots.ManualSlots();
                    if (quick == null && manual.Count == 0) _items.Add(Label(_template, _list, Tag + "Empty", "No saved games yet"));
                    if (quick != null) AddSlot(quick);
                    if (manual.Count > 0)
                    {
                        var section = Label(_template, _list, Tag + "Section", "<size=70%><alpha=#B0>MANUAL SAVES");
                        SetHeight(section, 36f);
                        _items.Add(section);
                        foreach (var s in manual) AddSlot(s);
                    }
                }
                Add("Back", ShowButtons);
            }

            private static void AddSlot(SaveSlots.Info slot)
            {
                string text = _armed == slot.Index
                    ? (_mode == Mode.Save ? "Overwrite " + slot.Title + "?  Click again" : "Load " + slot.Title + "?  Click again")
                    : Row(slot);
                Add(text, () => OnSlot(slot));
            }

            // "Slot 2   Parnu Invasion   03:11 · Sep 25 01:51" on one line; details slightly smaller.
            private static string Row(SaveSlots.Info s)
            {
                if (!s.Exists) return s.Title + "   <alpha=#80>Empty";
                return s.Title + "   " + s.Name + "   <size=75%><alpha=#B0>" + s.BattleTime + " · " + s.SavedLocal.ToString("MMM d HH:mm");
            }

            private static void OnSlot(SaveSlots.Info slot)
            {
                if (_mode == Mode.Save)
                {
                    if (!slot.Exists) { DoSave(slot); return; }
                    Confirm("Overwrite save", "Replace " + slot.Title + " (" + slot.Summary + ") with the current battle?",
                            "Overwrite", slot.Index, () => DoSave(slot));
                }
                else
                {
                    Confirm("Load game", "Load " + slot.Title + " (" + slot.Summary + ")?\nUnsaved progress in this battle will be lost.",
                            "Load", slot.Index, () => DoLoad(slot));
                }
            }

            private static void DoSave(SaveSlots.Info slot)
            {
                bool ok = Inspector.WriteSave(slot.Path);
                _status = ok ? "SAVED TO " + slot.Title.ToUpperInvariant() : "SAVE FAILED (see MelonLoader log)";
                _armed = -1;
                Render();
            }

            private static void DoLoad(SaveSlots.Info slot)
            {
                if (LoadFlow.Busy) { _status = "A LOAD IS ALREADY IN PROGRESS"; Render(); return; }
                Msg("loading " + slot.Path);
                LoadFlow.Begin(slot.Path);
            }

            // The game's own dialog; if it can't be shown, fall back to "click again" on the row.
            private static void Confirm(string title, string message, string yes, int slotIndex, Action onYes)
            {
                if (Popup.Show(_escape != null ? _escape._popupService : null, title, message, yes, "Cancel", onYes)) return;
                if (_armed == slotIndex) { _armed = -1; onYes(); return; }
                _armed = slotIndex;
                Render();
            }

            private static void ShowButtons()
            {
                foreach (var go in _items) if (go != null) UnityEngine.Object.Destroy(go);
                _items.Clear();
                foreach (var kv in _hidden) if (kv.Key != null) kv.Key.SetActive(kv.Value);
                _hidden.Clear();
                _mode = Mode.Buttons;
                _armed = -1;
                _status = null;
            }

            private static void Add(string text, Action onClick)
            {
                var go = Clone(_template, _list, Tag + "Item", text, onClick);
                go.SetActive(true);
                go.GetComponent<Button>().interactable = true;
                FitOneLine(go);
                _items.Add(go);
            }
        }

        // =====================================================================================
        // Main menu
        // =====================================================================================
        private static class MainMenu
        {
            private static IntPtr _screen;
            private static Transform _bar, _card;
            private static readonly List<GameObject> _items = new List<GameObject>();
            private static readonly List<KeyValuePair<GameObject, bool>> _hidden = new List<KeyValuePair<GameObject, bool>>();
            private static bool _listOpen;
            private static string _cardText;

            internal static void Tick()
            {
                MainMenuScreen screen = null;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MainMenuScreen>()))
                {
                    var s = o.TryCast<MainMenuScreen>();
                    if (s != null && s.gameObject.activeInHierarchy) { screen = s; break; }
                }
                if (screen == null) { _listOpen = false; return; }
                if (screen.Pointer == _screen && _card != null) { RefreshCard(); return; }
                Decorate(screen);
            }

            private static void Decorate(MainMenuScreen screen)
            {
                _bar = screen.transform.Find("Main menu content container/Home menu content/Left Side Bar");
                if (_bar == null) return;   // UI not built yet; try again next tick
                var template = _bar.Find("Play Campaign Button");
                if (template == null) { Warn("main menu: no Campaign card to clone"); _screen = screen.Pointer; return; }
                _screen = screen.Pointer;
                _items.Clear();
                _hidden.Clear();
                _targets.Clear();
                _listOpen = false;
                _cardText = null;

                // The column is laid out once by the game and not rebuilt, so place the card ourselves:
                // under the lowest visible card (Scenarios), same x, 12 px gap.
                var card = Card(template.gameObject, Tag + "Saved games", CardHeight, "Saved games", "", "Load", OpenList);
                card.transform.SetAsLastSibling();
                _card = card.transform;
                PlaceBelowLowest(card);
                RefreshCard();
                Msg("main menu: Saved games card added");
            }

            private static void RefreshCard()
            {
                if (_card == null || _listOpen) return;
                var latest = SaveSlots.Existing();
                string guide = latest.Count == 0 ? "Saved games" : "Saved games · " + latest[0].SavedLocal.ToString("MMM d HH:mm");
                string text = latest.Count == 0 ? "No saved games yet" : "Continue: " + latest[0].Name;
                if (text + guide == _cardText) return;
                _cardText = text + guide;
                SetText(_card, "Texts/GUIDE", guide);
                SetText(_card, "Texts/Text", text);
            }

            private static void OpenList()
            {
                if (_bar == null || _listOpen) return;
                var quick = SaveSlots.Quick();
                var manual = SaveSlots.ManualSlots();
                if (quick == null && manual.Count == 0) return;
                _hidden.Clear();
                for (int i = 0; i < _bar.childCount; i++)
                {
                    var go = _bar.GetChild(i).gameObject;
                    _hidden.Add(new KeyValuePair<GameObject, bool>(go, go.activeSelf));
                    go.SetActive(false);
                }
                _listOpen = true;
                var heights = new List<float>();
                if (quick != null) { _items.Add(SaveCard(quick)); heights.Add(ListCardHeight); }
                if (manual.Count > 0)
                {
                    _items.Add(SectionLabel("MANUAL SAVES")); heights.Add(LabelHeight);
                    foreach (var s in manual) { _items.Add(SaveCard(s)); heights.Add(ListCardHeight); }
                }
                _items.Add(Card(_card.gameObject, Tag + "Back", ListCardHeight, "", "Back", null, CloseList));
                heights.Add(ListCardHeight);
                // Stack from the top of the column; a bigger gap before the manual section.
                float y = 0f, x = _card.GetComponent<RectTransform>().anchoredPosition.x;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].name == Tag + "Section" && i > 0) y -= Gap;
                    Place(_items[i], new Vector2(x, y));
                    y -= heights[i] + Gap;
                }
            }

            // Header line: "SLOT 2 · SEP 25 01:56"; main line: "Parnu Invasion · 04:35".
            private static GameObject SaveCard(SaveSlots.Info slot)
            {
                return Card(_card.gameObject, Tag + "Save item", ListCardHeight,
                    slot.Title + " · " + slot.SavedLocal.ToString("MMM d HH:mm"), slot.Name + " · " + slot.BattleTime, "", () =>
                    {
                        if (LoadFlow.Busy) return;
                        Msg("loading " + slot.Path);
                        LoadFlow.Begin(slot.Path);
                    });
            }

            /// <summary>A plain section heading in the column: card text style, no background, not clickable.</summary>
            private static GameObject SectionLabel(string text)
            {
                var go = Card(_card.gameObject, Tag + "Section", LabelHeight, "", text, null, null);
                var ui = go.GetComponent<UiButton>();
                if (ui != null) UnityEngine.Object.DestroyImmediate(ui);
                var button = go.GetComponent<Button>();
                if (button != null) UnityEngine.Object.DestroyImmediate(button);
                foreach (string child in new[] { "image", "Hover background", "Border Base", "Border Blur" })
                {
                    var t = go.transform.Find(child);
                    if (t != null) t.gameObject.SetActive(false);
                }
                var guide = go.transform.Find("Texts/GUIDE")?.GetComponent<TextMeshProUGUI>();
                var main = go.transform.Find("Texts/Text")?.GetComponent<TextMeshProUGUI>();
                if (guide != null && main != null)
                {
                    main.enableAutoSizing = false;
                    main.fontSize = guide.fontSize;
                    main.color = guide.color;
                    main.raycastTarget = false;
                }
                return go;
            }

            private static void CloseList()
            {
                foreach (var go in _items) if (go != null) { _targets.Remove(go.Pointer); UnityEngine.Object.Destroy(go); }
                _items.Clear();
                foreach (var kv in _hidden) if (kv.Key != null) kv.Key.SetActive(kv.Value);
                _hidden.Clear();
                _listOpen = false;
                _cardText = null;
                RefreshCard();
            }

            private const float CardHeight = 88f, ListCardHeight = 76f, LabelHeight = 36f, Gap = 10f;

            // Where our cards belong. The cloned card's own UI component puts it back at the
            // template's position after we move it, so KeepPlaced re-applies these every tick.
            private static readonly Dictionary<IntPtr, Vector2> _targets = new Dictionary<IntPtr, Vector2>();
            private static bool _loggedMove;

            private static void Place(GameObject go, Vector2 pos)
            {
                var rt = go.GetComponent<RectTransform>();
                rt.anchoredPosition = pos;
                _targets[go.Pointer] = pos;
            }

            internal static void KeepPlaced()
            {
                if (_targets.Count == 0) return;
                var live = new List<GameObject>(_items);
                if (_card != null) live.Add(_card.gameObject);
                foreach (var go in live)
                {
                    if (go == null || !_targets.TryGetValue(go.Pointer, out var want)) continue;
                    var rt = go.GetComponent<RectTransform>();
                    if ((rt.anchoredPosition - want).sqrMagnitude < 0.25f) continue;
                    if (!_loggedMove) { _loggedMove = true; Msg("main menu: game moved our card to " + rt.anchoredPosition + "; keeping it at " + want); }
                    rt.anchoredPosition = want;
                }
            }

            private static void PlaceBelowLowest(GameObject card)
            {
                float bottom = 0f, x = 0f;
                for (int i = 0; i < _bar.childCount; i++)
                {
                    var t = _bar.GetChild(i);
                    if (!t.gameObject.activeSelf || t.gameObject == card) continue;
                    var rt = t.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    float b = rt.anchoredPosition.y - rt.sizeDelta.y;
                    if (b < bottom) { bottom = b; x = rt.anchoredPosition.x; }
                }
                Place(card, new Vector2(x, bottom - Gap));
                Msg("main menu: Saved games card placed at y=" + (bottom - Gap));
            }

            /// <summary>Clone a Home-tab card: dark panel instead of the campaign art (it also takes the clicks), our texts and click.</summary>
            private static GameObject Card(GameObject template, string name, float height, string guide, string text, string launch, Action onClick)
            {
                var go = Clone(template, _bar, name, null, onClick);
                go.SetActive(true);
                var art = go.transform.Find("image");
                var artImg = art != null ? art.GetComponent<Image>() : null;
                if (artImg != null)
                {
                    artImg.sprite = null;
                    artImg.color = new Color(0f, 0f, 0f, 0.6f);
                    artImg.raycastTarget = true;
                    art.gameObject.SetActive(true);
                }
                SetText(go.transform, "Texts/GUIDE", guide);
                SetText(go.transform, "Texts/Text", text);
                foreach (string path in new[] { "Texts/GUIDE", "Texts/Text" })
                {
                    var tmp = go.transform.Find(path)?.GetComponent<TextMeshProUGUI>();
                    if (tmp == null) continue;
                    float size = tmp.fontSize;
                    tmp.enableWordWrapping = false;
                    tmp.enableAutoSizing = true;
                    tmp.fontSizeMax = size;
                    tmp.fontSizeMin = Mathf.Max(10f, size * 0.7f);
                    tmp.overflowMode = TextOverflowModes.Ellipsis;   // stays inside its 380 px box, clear of the arrow
                }
                var launchBox = go.transform.Find("Launch Container");
                if (launchBox != null)
                {
                    if (launch == null) launchBox.gameObject.SetActive(false);
                    else SetText(go.transform, "Launch Container/Text", launch);
                }
                SetHeight(go, height);
                return go;
            }
        }

        // =====================================================================================
        // The game's confirmation dialog
        // =====================================================================================
        private static class Popup
        {
            private static UiConfig _config;
            private static readonly List<Il2CppSystem.Delegate> _keepAlive = new List<Il2CppSystem.Delegate>();

            /// <summary>Show the game's dialog (title, message, Yes/No). False if it couldn't be shown.</summary>
            internal static bool Show(PopupService service, string title, string message, string yes, string no, Action onYes)
            {
                try
                {
                    if (service == null) return false;
                    var cfg = Config();
                    if (cfg == null || cfg.PopupPrefab == null) { Warn("popup: no UiConfig.PopupPrefab"); return false; }

                    // One-shot: the confirm can reach us through several routes (see below).
                    bool fired = false;
                    Action once = () =>
                    {
                        if (fired) return;
                        fired = true;
                        Msg("popup confirmed: " + title);
                        try { onYes(); } catch (Exception e) { Warn("popup action threw: " + e); }
                    };
                    var apply = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Il2CppSystem.Object>>(new Action<Il2CppSystem.Object>(_ => once()));
                    var click = DelegateSupport.ConvertDelegate<UnityAction>(once);
                    _keepAlive.Add(apply);
                    _keepAlive.Add(click);
                    if (_keepAlive.Count > 32) _keepAlive.RemoveRange(0, 2);

                    var p = new PopupCreationParams();
                    p.PopupPrefab = cfg.PopupPrefab;
                    p.FadeVelocity = cfg.PopupFadeAnimationVelocity;
                    p.BackgroundColor = cfg.PopupBackgroundColor;
                    p.UseBlur = true;
                    // Loc keys: the dialog localizes these; our literal text is written over them below.
                    p.TitleLocKey = title;
                    p.MessageLocKey = message;
                    p.YesButtonTextLocKey = yes;
                    p.NoButtonTextLocKey = no;
                    p.ApplyAction = apply;

                    var go = service.ShowPopup(ref p);
                    if (go == null) return false;
                    var dialog = go.GetComponentInChildren<UiPopupDialogue>(true);
                    foreach (var tt in go.GetComponentsInChildren<TextTarget>(true)) UnityEngine.Object.DestroyImmediate(tt);
                    if (dialog != null)
                    {
                        // PopupCreationParams.ApplyAction alone never fired in testing: subscribe to the
                        // dialog's own Apply event and to the apply button's click as well.
                        try { dialog.add_ApplyEvent(apply); } catch (Exception e) { Warn("popup ApplyEvent: " + e.Message); }
                        try
                        {
                            var btn = dialog._applyButton != null ? dialog._applyButton.GetComponent<Button>() : null;
                            if (btn != null) btn.onClick.AddListener(click);
                        }
                        catch (Exception e) { Warn("popup apply button: " + e.Message); }
                        if (dialog._title != null) dialog._title.text = title;
                        if (dialog._description != null) dialog._description.text = message;
                        if (dialog._applyText != null) dialog._applyText.text = yes;
                        if (dialog._cancelText != null) dialog._cancelText.text = no;
                    }
                    return true;
                }
                catch (Exception e) { Warn("popup failed, using click-again: " + (e.InnerException ?? e).Message); return false; }
            }

            private static UiConfig Config()
            {
                if (_config != null) return _config;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<UiConfig>()))
                {
                    _config = o.TryCast<UiConfig>();
                    if (_config != null) break;
                }
                return _config;
            }
        }

        // =====================================================================================
        // helpers
        // =====================================================================================

        /// <summary>Instantiate a copy of a game widget: new name, localization stripped, our label and click.</summary>
        private static GameObject Clone(GameObject template, Transform parent, string name, string label, Action onClick)
        {
            var go = UnityEngine.Object.Instantiate(template, parent, false).Cast<GameObject>();
            go.name = name;
            foreach (var tt in go.GetComponentsInChildren<TextTarget>(true)) UnityEngine.Object.DestroyImmediate(tt);
            if (label != null)
            {
                var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null) tmp.text = label;
            }
            var button = go.GetComponent<Button>();
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();   // drop the original's listeners
                if (onClick != null)
                {
                    Action safe = () =>
                    {
                        try { onClick(); }
                        catch (Exception e) { Warn("click on '" + name + "' threw: " + e); }
                    };
                    button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(safe));
                }
            }
            return go;
        }

        /// <summary>A heading in the button list's style: same text, no background, not clickable or hoverable.</summary>
        private static GameObject Label(GameObject template, Transform parent, string name, string text)
        {
            var go = Clone(template, parent, name, text, null);
            go.SetActive(true);
            var ui = go.GetComponent<UiButton>();
            if (ui != null) UnityEngine.Object.DestroyImmediate(ui);
            var button = go.GetComponent<Button>();
            if (button != null) UnityEngine.Object.DestroyImmediate(button);
            var bg = go.GetComponent<Image>();
            if (bg != null) bg.enabled = false;
            var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.raycastTarget = false;
            FitOneLine(go);
            return go;
        }

        /// <summary>Keep a row's text on one line: shrink to fit, ellipsis if still too long.</summary>
        private static void FitOneLine(GameObject go)
        {
            var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp == null) return;
            float size = tmp.fontSize;
            tmp.richText = true;
            tmp.enableWordWrapping = false;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax = size;
            tmp.fontSizeMin = Mathf.Max(10f, size * 0.6f);
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            var rt = tmp.rectTransform;
            rt.offsetMin = new Vector2(24f, rt.offsetMin.y);    // keep clear of the button's edges
            rt.offsetMax = new Vector2(-24f, rt.offsetMax.y);
        }

        private static void SetText(Transform root, string path, string text)
        {
            var t = root.Find(path);
            var tmp = t != null ? t.GetComponent<TextMeshProUGUI>() : null;
            if (tmp != null) tmp.text = text ?? "";
        }

        private static void SetHeight(GameObject go, float height)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }

        private static void Msg(string s) => MelonLogger.Msg("[ui] " + s);
        private static void Warn(string s) => MelonLogger.Warning("[ui] " + s);
    }
}
