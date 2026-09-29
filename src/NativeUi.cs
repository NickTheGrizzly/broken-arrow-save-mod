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
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController

namespace BASaveGame
{
    /// <summary>
    /// Native Save/Load UI, built from the game's own widgets so it looks and behaves like the rest
    /// of the menus (layout from the F3 dumps):
    ///  - Main menu (MainMenuScreen, Home tab): a "Saved games" card cloned from the Campaign card
    ///    in the left column; it swaps the column for a list of save cards, newest first.
    ///  - Pause menu (EscapeMenu): "Save game" / "Load game" buttons cloned from Settings, placed
    ///    after "Restart mission". Clicking swaps the button list for a heading + the same save
    ///    cards + Back. The card template is a hidden copy of the Campaign card kept from the main
    ///    menu (battles are always launched from there); without it the lists fall back to plain
    ///    pause-menu rows.
    ///  - Every existing save has a bin button (delete, after confirmation).
    /// Confirmations use the game's own dialog (UiPopupDialogue via PopupService, prefab + colours
    /// from UiConfig), falling back to "click again" if that can't be shown.
    /// Cloned texts lose their TextTarget (localization) component, which would otherwise put the
    /// original label back. Polled from OnUpdate (no Harmony); each menu instance is decorated once.
    /// </summary>
    internal static class NativeUi
    {
        private const string Tag = "BASave ";
        private const float CardHeight = 88f, ListCardHeight = 76f, LabelHeight = 36f, Gap = 10f;
        private static float _nextCheck;

        internal static void Tick()
        {
            try { KeepPinned(); } catch { }   // every frame, so a moved card never shows in the wrong spot
            try { EscapeBack(); } catch { }
            float now = Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + 0.3f;
            // Only look for the menu that can exist: the pause menu in a battle, the main menu outside one.
            bool battle = GameController.IsInstanceAlive;
            if (battle || PauseMenu.Open) { try { PauseMenu.Tick(); } catch (Exception e) { Warn("pause menu: " + e.Message); } }
            if (!battle) { try { MainMenu.Tick(); } catch (Exception e) { Warn("main menu: " + e.Message); } }
        }

        // Esc backs out of our lists. Acted on the frame AFTER the key press: going back re-shows the
        // pause menu's Resume button, whose ClickByHotKey would otherwise see the same Esc press and
        // close the whole menu. Ignored while one of the game's dialogs is up (Esc closes that).
        private static bool _escPending;

        private static void EscapeBack()
        {
            if (_escPending)
            {
                _escPending = false;
                if (PauseMenu.Open) PauseMenu.Back();
                else if (MainMenu.ListOpen) MainMenu.Back();
                return;
            }
            if (!PauseMenu.Open && !MainMenu.ListOpen) return;
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UiPopupDialogue>()))
            {
                var d = o.TryCast<UiPopupDialogue>();
                if (d != null && d.gameObject.activeInHierarchy) return;
            }
            _escPending = true;
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
            private static GameObject _template;            // our "Save game" button, reused for headings / fallback rows
            private static Mode _mode = Mode.Buttons;
            private static readonly List<GameObject> _items = new List<GameObject>();
            private static readonly List<KeyValuePair<GameObject, bool>> _hidden = new List<KeyValuePair<GameObject, bool>>();
            private static int _armed = -1;                 // fallback confirmation when the dialog isn't available
            private static string _status;

            internal static bool Open => _mode != Mode.Buttons;

            internal static void Back() => ShowButtons();

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
                    if (_mode != Mode.Buttons) ShowButtons();   // menu closed while a list was open
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

                // Unsupported missions: Save is dimmed and only explains why.
                bool unsupported = SaveSlots.CurrentUnsupported;
                var save = Clone(settings.gameObject, _list, Tag + "Save", "Save game", () =>
                {
                    if (unsupported) Notify.Error("Saving isn't available in this mission (" + SaveSlots.CurrentScenario() + ").");
                    else ShowSlots(Mode.Save);
                });
                save.transform.SetSiblingIndex(at);
                if (unsupported)
                {
                    var label = save.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (label != null) label.alpha = 0.35f;
                }
                var load = Clone(settings.gameObject, _list, Tag + "Load", "Load game", () => ShowSlots(Mode.Load));
                load.transform.SetSiblingIndex(at + 1);
                _template = save;

                // Make room: hide the list's empty spacer slots, and put Resume right under Settings
                // (our two buttons otherwise push it off the bottom of the menu).
                for (int i = 0; i < _list.childCount; i++)
                {
                    var child = _list.GetChild(i);
                    if (child.name.EndsWith("Empty Slot")) child.gameObject.SetActive(false);
                }
                var resume = _list.Find("Button (resume)");
                if (resume != null)
                {
                    // A gap above Resume (~100 px with the list's own spacing), then Resume and the rest.
                    var gap = new GameObject(Tag + "Gap");
                    gap.transform.SetParent(_list, false);
                    gap.AddComponent<RectTransform>();
                    SetHeight(gap, 70f);
                    gap.transform.SetSiblingIndex(settings.transform.GetSiblingIndex() + 1);
                    resume.SetSiblingIndex(gap.transform.GetSiblingIndex() + 1);
                }
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
                Clear();
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
                if (Cards.Template != null) _items.Add(CardRow("", "Back", null, ShowButtons));
                else Add("Back", ShowButtons);
            }

            private static void AddSlot(SaveSlots.Info slot)
            {
                if (Cards.Template == null) { AddPlainSlot(slot); return; }
                bool armed = _armed == slot.Index;
                string guide = slot.Exists ? slot.Title + " · " + slot.SavedLocal.ToString("MMM d HH:mm") : slot.Title;
                string text = armed ? (_mode == Mode.Save ? "Overwrite? Click again" : "Load? Click again")
                            : slot.Exists ? slot.Label : "<alpha=#80>Empty";
                string launch = _mode == Mode.Save ? (slot.Exists ? "Overwrite" : "Save") : "Load";
                var row = CardRow(guide, text, launch, () => OnSlot(slot));
                if (slot.Exists) AddDeleteButton(row.transform.GetChild(0).gameObject, slot, Service, Render);
                _items.Add(row);
            }

            // Plain pause-menu row, when there's no card template: "Slot 2   Parnu Invasion   03:11 · Sep 25 01:51".
            private static void AddPlainSlot(SaveSlots.Info s)
            {
                string text;
                if (_armed == s.Index) text = (_mode == Mode.Save ? "Overwrite " : "Load ") + s.Title + "?  Click again";
                else if (!s.Exists) text = s.Title + "   <alpha=#80>Empty";
                else if (s.Problem != null) text = s.Title + "   " + s.Name + "   <size=75%><alpha=#80>(" + s.Problem + ")";
                else text = s.Title + "   " + s.Name + "   <size=75%><alpha=#B0>" + s.BattleTime + " · " + s.SavedLocal.ToString("MMM d HH:mm");
                Add(text, () => OnSlot(s));
            }

            /// <summary>A save card in the vertical button list: a layout row holding a pinned, centred card.</summary>
            private static GameObject CardRow(string guide, string text, string launch, Action onClick)
            {
                var row = new GameObject(Tag + "Row");
                row.transform.SetParent(_list, false);
                row.AddComponent<RectTransform>();
                SetHeight(row, ListCardHeight);
                var card = Card(Cards.Template, row.transform, Tag + "Card", ListCardHeight, guide, text, launch, onClick, staticLaunch: true);
                var rt = card.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                Pin(card, Vector2.zero);
                return row;
            }

            private static PopupService Service
            {
                get
                {
                    var s = _escape != null ? _escape._popupService : null;
                    if (s != null) Popup.Remember(s);
                    return s ?? Popup.FindService();
                }
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
                    if (slot.Problem != null) { _status = "Can't load " + slot.Title + ": " + slot.Problem; Render(); return; }
                    if (LoadFlow.Busy) { _status = "A saved game is already loading"; Render(); return; }
                    Confirm("Load game", "Load " + slot.Title + " (" + slot.Summary + ")?\nUnsaved progress in this battle will be lost.",
                            "Load", slot.Index, () => DoLoad(slot));
                }
            }

            private static void DoSave(SaveSlots.Info slot)
            {
                bool ok = Inspector.WriteSave(slot.Path, out string error);
                _status = ok ? "SAVED TO " + slot.Title.ToUpperInvariant() : error;
                _armed = -1;
                Render();
            }

            private static void DoLoad(SaveSlots.Info slot)
            {
                Msg("loading " + slot.Path);
                string error = LoadFlow.Begin(slot.Path);
                if (error != null) { _status = error; Render(); }
            }

            // The game's own dialog; if it can't be shown, fall back to "click again" on the row.
            private static void Confirm(string title, string message, string yes, int slotIndex, Action onYes)
            {
                if (Popup.Show(Service, title, message, yes, "Cancel", onYes)) return;
                if (_armed == slotIndex) { _armed = -1; onYes(); return; }
                _armed = slotIndex;
                Render();
            }

            private static void ShowButtons()
            {
                Clear();
                foreach (var kv in _hidden) if (kv.Key != null) kv.Key.SetActive(kv.Value);
                _hidden.Clear();
                _mode = Mode.Buttons;
                _armed = -1;
                _status = null;
            }

            private static void Clear()
            {
                foreach (var go in _items) if (go != null) UnityEngine.Object.Destroy(go);
                _items.Clear();
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

            internal static bool ListOpen => _listOpen;

            internal static void Back() => CloseList();

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
                _listOpen = false;
                _cardText = null;
                Cards.Capture(template.gameObject);   // for the pause-menu lists

                // The column is laid out once by the game and not rebuilt, so place the card ourselves:
                // under the lowest visible card (Scenarios), same x, 12 px gap.
                var card = Card(template.gameObject, _bar, Tag + "Saved games", CardHeight, "Saved games", "", "Load", OpenList);
                card.transform.SetAsLastSibling();
                _card = card.transform;
                PlaceBelowLowest(card);
                RefreshCard();
                Msg("main menu: Saved games card added");
            }

            private static void RefreshCard()
            {
                if (_card == null || _listOpen) return;
                var latest = SaveSlots.Existing().FindAll(x => x.Problem == null);
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
                _items.Add(Card(_card.gameObject, _bar, Tag + "Back", ListCardHeight, "", "Back", null, CloseList));
                heights.Add(ListCardHeight);
                // Stack from the top of the column; a bigger gap before the manual section.
                float y = 0f, x = _card.GetComponent<RectTransform>().anchoredPosition.x;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].name == Tag + "Section" && i > 0) y -= Gap;
                    Pin(_items[i], new Vector2(x, y));
                    y -= heights[i] + Gap;
                }
            }

            // Header line: "SLOT 2 · SEP 25 01:56"; main line: "Parnu Invasion · 04:35"; "Load" on hover.
            private static GameObject SaveCard(SaveSlots.Info slot)
            {
                var card = Card(_card.gameObject, _bar, Tag + "Save item", ListCardHeight,
                    slot.Title + " · " + slot.SavedLocal.ToString("MMM d HH:mm"), slot.Label, "Load", () =>
                    {
                        // Begin refuses (with a message) when a load is running or the save can't be loaded.
                        Msg("loading " + slot.Path);
                        LoadFlow.Begin(slot.Path);
                    }, staticLaunch: true);
                AddDeleteButton(card, slot, null, Reopen);
                return card;
            }

            /// <summary>Rebuild the list after a delete (or go back to the column if nothing is left).</summary>
            private static void Reopen()
            {
                CloseList();
                OpenList();
            }

            /// <summary>A plain section heading in the column: card text style, no background, not clickable.</summary>
            private static GameObject SectionLabel(string text)
            {
                var go = Card(_card.gameObject, _bar, Tag + "Section", LabelHeight, "", text, null, null);
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
                foreach (var go in _items) if (go != null) { Unpin(go); UnityEngine.Object.Destroy(go); }
                _items.Clear();
                foreach (var kv in _hidden) if (kv.Key != null) kv.Key.SetActive(kv.Value);
                _hidden.Clear();
                _listOpen = false;
                _cardText = null;
                RefreshCard();
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
                Pin(card, new Vector2(x, bottom - Gap));
                Msg("main menu: Saved games card placed at y=" + (bottom - Gap));
            }
        }

        // =====================================================================================
        // Cards (shared by both menus)
        // =====================================================================================

        /// <summary>A hidden copy of the main menu's Campaign card, kept across scenes for the pause menu.</summary>
        private static class Cards
        {
            private static GameObject _holder, _template;

            internal static GameObject Template => _template != null ? _template : null;

            internal static void Capture(GameObject campaignCard)
            {
                if (_template != null) return;
                try
                {
                    // Inactive holder: the copy never runs (no Awake/OnEnable) until it's cloned into a menu.
                    _holder = new GameObject(Tag + "Templates");
                    _holder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                    _template = UnityEngine.Object.Instantiate(campaignCard, _holder.transform, false).Cast<GameObject>();
                    _template.name = Tag + "Card template";
                    Msg("card template kept for the pause menu");
                }
                catch (Exception e) { Warn("card template: " + e.Message); }
            }
        }

        // Right-hand cluster of a list card, from the right edge: bin, play arrow, static label.
        private const float BinCenter = 24f, ArrowCenter = 58f, LabelRight = 78f;

        /// <summary>
        /// Clone a Home-tab card: dark panel instead of the campaign art (it also takes the clicks), our texts and click.
        /// <paramref name="staticLaunch"/>: replace the animated "Launch" pop-out (it slides over the main
        /// text) with a static label + arrow at the right, leaving room for the bin.
        /// </summary>
        private static GameObject Card(GameObject template, Transform parent, string name, float height, string guide, string text, string launch, Action onClick, bool staticLaunch = false)
        {
            var go = Clone(template, parent, name, null, onClick);
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
                tmp.richText = true;
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
                else if (staticLaunch) StaticLaunch(go, launchBox, launch);
                else SetText(go.transform, "Launch Container/Text", launch);
            }
            SetHeight(go, height);
            return go;
        }

        /// <summary>Copy the pop-out's label and arrow onto the card as plain, unanimated children; hide the pop-out.</summary>
        private static void StaticLaunch(GameObject card, Transform launchBox, string launch)
        {
            try
            {
                float labelWidth = 60f;
                var srcText = launchBox.Find("Text")?.GetComponent<TextMeshProUGUI>();
                if (srcText != null)
                {
                    var label = UnityEngine.Object.Instantiate(srcText.gameObject, card.transform, false).Cast<GameObject>();
                    label.name = Tag + "Launch label";
                    var tmp = label.GetComponent<TextMeshProUGUI>();
                    tmp.text = launch;
                    tmp.enableWordWrapping = false;
                    tmp.alignment = TextAlignmentOptions.MidlineRight;
                    tmp.raycastTarget = false;
                    try { labelWidth = Mathf.Clamp(tmp.GetPreferredValues(launch).x + 4f, 30f, 140f); } catch { }
                    var rt = tmp.rectTransform;
                    rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
                    rt.pivot = new Vector2(1f, 0.5f);
                    rt.anchoredPosition = new Vector2(-LabelRight, 0f);
                    rt.sizeDelta = new Vector2(labelWidth, 30f);
                }
                var srcIcon = launchBox.Find("Play Icon (blur)");
                if (srcIcon != null)
                {
                    var icon = UnityEngine.Object.Instantiate(srcIcon.gameObject, card.transform, false).Cast<GameObject>();
                    icon.name = Tag + "Launch arrow";
                    var rt = icon.GetComponent<RectTransform>();
                    rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = new Vector2(-ArrowCenter, 0f);
                    rt.sizeDelta = new Vector2(28f, 28f);
                    foreach (var g in icon.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                }
                launchBox.gameObject.SetActive(false);

                // Keep the main line clear of the cluster (it shrinks, then ellipsizes).
                var main = card.transform.Find("Texts/Text")?.GetComponent<RectTransform>();
                if (main != null)
                {
                    float cardWidth = card.GetComponent<RectTransform>().sizeDelta.x;
                    if (cardWidth <= 0f) cardWidth = 512f;
                    float room = cardWidth - main.anchoredPosition.x - LabelRight - labelWidth - 12f;
                    if (room > 100f && room < main.sizeDelta.x) main.sizeDelta = new Vector2(room, main.sizeDelta.y);
                }
            }
            catch (Exception e)
            {
                Warn("static launch label: " + e.Message);
                SetText(card.transform, "Launch Container/Text", launch);
            }
        }

        // Where our cards belong. A cloned card's own UI component puts it back at the template's
        // position after we move it, so KeepPinned re-applies these every frame.
        private static readonly Dictionary<IntPtr, KeyValuePair<GameObject, Vector2>> _pins = new Dictionary<IntPtr, KeyValuePair<GameObject, Vector2>>();
        private static bool _loggedMove;

        private static void Pin(GameObject go, Vector2 pos)
        {
            go.GetComponent<RectTransform>().anchoredPosition = pos;
            _pins[go.Pointer] = new KeyValuePair<GameObject, Vector2>(go, pos);
        }

        private static void Unpin(GameObject go) => _pins.Remove(go.Pointer);

        private static void KeepPinned()
        {
            if (_pins.Count == 0) return;
            List<IntPtr> gone = null;
            foreach (var kv in _pins)
            {
                var go = kv.Value.Key;
                if (go == null) { (gone ??= new List<IntPtr>()).Add(kv.Key); continue; }
                var rt = go.GetComponent<RectTransform>();
                Vector2 want = kv.Value.Value;
                if ((rt.anchoredPosition - want).sqrMagnitude < 0.25f) continue;
                if (!_loggedMove) { _loggedMove = true; Msg("game moved our card to " + rt.anchoredPosition + "; keeping it at " + want); }
                rt.anchoredPosition = want;
            }
            if (gone != null) foreach (var p in gone) _pins.Remove(p);
        }

        // =====================================================================================
        // Delete (bin button + confirmation)
        // =====================================================================================
        private static string _armedDelete;
        private static float _armedDeleteUntil;

        /// <summary>A small bin button in the card's empty right-hand strip; asks before deleting the save.</summary>
        private static void AddDeleteButton(GameObject card, SaveSlots.Info slot, PopupService service, Action after)
        {
            try
            {
                var go = new GameObject(Tag + "Delete");
                go.transform.SetParent(card.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(-BinCenter, 0f);
                rt.sizeDelta = new Vector2(24f, 24f);
                var img = go.AddComponent<Image>();
                img.sprite = BinSprite();
                img.preserveAspect = true;
                img.color = new Color(1f, 1f, 1f, 0.55f);
                var button = go.AddComponent<Button>();
                button.targetGraphic = img;
                var colors = button.colors;
                colors.highlightedColor = new Color(1f, 0.45f, 0.4f, 1.6f);
                colors.pressedColor = new Color(1f, 0.3f, 0.25f, 1.8f);
                button.colors = colors;
                Action click = () =>
                {
                    try { AskDelete(slot, service, img, after); }
                    catch (Exception e) { Warn("delete threw: " + e); }
                };
                button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(click));
            }
            catch (Exception e) { Warn("delete button: " + e.Message); }
        }

        private static void AskDelete(SaveSlots.Info slot, PopupService service, Image icon, Action after)
        {
            if (LoadFlow.Busy) { Notify.Error("Can't delete saves while a saved game is loading."); return; }
            Action delete = () =>
            {
                _armedDelete = null;
                if (SaveSlots.Delete(slot, out string error)) Notify.Info("Deleted " + slot.Title, "[save] deleted " + slot.Title + " (" + slot.Path + ")");
                else Notify.Error("Couldn't delete " + slot.Title + ": " + error, "[save] delete " + slot.Path + " failed: " + error);
                after?.Invoke();
            };
            if (Popup.Show(service ?? Popup.FindService(), "Delete save",
                           "Delete " + slot.Title + " (" + slot.Summary + ")?\nThis can't be undone.", "Delete", "Cancel", delete))
                return;
            // No dialog available: second click within 3 s deletes.
            float now = Time.realtimeSinceStartup;
            if (_armedDelete == slot.Path && now < _armedDeleteUntil) { delete(); return; }
            _armedDelete = slot.Path;
            _armedDeleteUntil = now + 3f;
            if (icon != null) icon.color = new Color(1f, 0.35f, 0.3f, 1f);
            Notify.Hint("Click the bin again to delete " + slot.Title);
        }

        private static Sprite _bin;

        /// <summary>The game's "Delete Icon" sprite if it's loaded, else a small bin drawn once at runtime.</summary>
        private static Sprite BinSprite()
        {
            if (_bin != null) return _bin;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
                {
                    var s = o.TryCast<Sprite>();
                    if (s != null && s.name == "Delete Icon") { _bin = s; Msg("bin icon: game sprite 'Delete Icon'"); return _bin; }
                }
            }
            catch { }
            const int N = 32;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var clear = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    tex.SetPixel(x, y, BinPixel(x, y) ? Color.white : clear);
            tex.Apply();
            _bin = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            _bin.hideFlags = HideFlags.HideAndDontSave;
            Msg("bin icon: drawn");
            return _bin;
        }

        // 32x32 bin, y = 0 at the bottom: handle, lid, and an outlined body with three slits.
        private static bool BinPixel(int x, int y)
        {
            if (y >= 27 && y <= 29 && x >= 12 && x <= 19) return y == 29 || x <= 13 || x >= 18;
            if (y >= 23 && y <= 25) return x >= 5 && x <= 26;
            if (y >= 3 && y <= 20 && x >= 8 && x <= 23)
            {
                if (x <= 9 || x >= 22 || y <= 4) return true;
                return y >= 7 && y <= 17 && (x == 12 || x == 13 || x == 15 || x == 16 || x == 18 || x == 19);
            }
            return false;
        }

        // =====================================================================================
        // The game's confirmation dialog
        // =====================================================================================
        private static class Popup
        {
            private static UiConfig _config;
            private static readonly List<Il2CppSystem.Delegate> _keepAlive = new List<Il2CppSystem.Delegate>();

            private static PopupService _known;

            internal static void Remember(PopupService s) { if (s != null) _known = s; }

            /// <summary>
            /// A PopupService when no pause menu is at hand (main menu). Tried in order: the service
            /// locator, the DI containers (VContainer LifetimeScopes), then game screens that hold
            /// one; the last one that worked is reused while it still has its popup canvas.
            /// </summary>
            internal static PopupService FindService()
            {
                try { if (_known != null && _known.PopupCanvasGo != null) return _known; } catch { }
                _known = null;
                try
                {
                    var o = Il2CppBrokenArrow.Shared.Ecs.Services.Session.GetService(Il2CppType.Of<PopupService>());
                    var s = o?.TryCast<PopupService>();
                    if (s != null) { Msg("popup service: session"); return _known = s; }
                }
                catch { }
                try
                {
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppVContainer.Unity.LifetimeScope>()))
                    {
                        var scope = o.TryCast<Il2CppVContainer.Unity.LifetimeScope>();
                        var c = scope?.Container;
                        if (c == null) continue;
                        try
                        {
                            if (c.TryResolve(Il2CppType.Of<PopupService>(), out Il2CppSystem.Object r, null) && r?.TryCast<PopupService>() is PopupService s)
                            { Msg("popup service: container of '" + scope.gameObject.name + "'"); return _known = s; }
                        }
                        catch { }
                    }
                }
                catch (Exception e) { Warn("popup service (containers): " + e.Message); }
                try
                {
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Il2CppBrokenArrow.Client.Ecs.UI.Menu.Settings.SettingsScreen>()))
                    {
                        var s = o.TryCast<Il2CppBrokenArrow.Client.Ecs.UI.Menu.Settings.SettingsScreen>();
                        if (s != null && s._popupService != null) { Msg("popup service: settings screen"); return _known = s._popupService; }
                    }
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Il2CppBrokenArrow.Client.Ecs.UI.Menu.Arsenal.ArmyBuilder.DeckListPanel>()))
                    {
                        var s = o.TryCast<Il2CppBrokenArrow.Client.Ecs.UI.Menu.Arsenal.ArmyBuilder.DeckListPanel>();
                        if (s != null && s._popupService != null) { Msg("popup service: deck list"); return _known = s._popupService; }
                    }
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Il2CppBrokenArrow.Client.Ecs.UI.Menu.Editor.Scenarios.ScenarioScreen>()))
                    {
                        var s = o.TryCast<Il2CppBrokenArrow.Client.Ecs.UI.Menu.Editor.Scenarios.ScenarioScreen>();
                        if (s != null && s._popupService != null) { Msg("popup service: scenario screen"); return _known = s._popupService; }
                    }
                }
                catch (Exception e) { Warn("popup service lookup: " + e.Message); }
                Warn("no popup service found; confirmations use click-again");
                return null;
            }

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

        private static void Msg(string s) => ModLog.Dev("[ui] " + s);
        private static void Warn(string s) => ModLog.Warn("[ui] " + s);
    }
}
