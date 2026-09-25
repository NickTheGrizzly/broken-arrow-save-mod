using System;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace BASaveGame
{
    /// <summary>
    /// RECON (native Save/Load UI): F3 writes the hierarchy of the pause menu (EscapeMenu) and the
    /// main menu (MainMenuScreen) — names, active flags, components, rect layout, button texts —
    /// to Saves\ui_dump_*.txt, so cloned Save/Load buttons can be placed like the game's own.
    /// </summary>
    internal static class UiDump
    {
        private const int MaxDepth = 9;

        internal static void Dump()
        {
            string path = Path.Combine(SaveMod.SaveDir, "ui_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            using var w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            w.WriteLine("==== UI dump @ " + DateTime.Now.ToString("s") + "  scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            int found = 0;
            found += DumpAll<Il2CppBrokenArrow.Client.Ecs.UI.Menu.Profile.EscapeMenu>(w, "EscapeMenu");
            found += DumpAll<Il2CppBrokenArrow.Client.Ecs.UI.MainMenuScreen>(w, "MainMenuScreen");
            w.WriteLine("==== end (" + found + " root(s)) ====");
            MelonLogger.Msg("[ui] dumped " + found + " menu root(s) -> " + path);
        }

        private static int DumpAll<T>(StreamWriter w, string label) where T : Il2CppSystem.Object
        {
            int n = 0;
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<T>()))
                {
                    var c = o.TryCast<Component>();
                    if (c == null) continue;
                    n++;
                    w.WriteLine();
                    w.WriteLine("---- " + label + " on '" + c.gameObject.name + "' ----");
                    // Also show the parent chain so we know where the menu sits in the canvas.
                    var sb = new StringBuilder();
                    for (Transform p = c.transform.parent; p != null; p = p.parent) sb.Insert(0, p.name + " / ");
                    w.WriteLine("path: " + sb + c.gameObject.name);
                    Walk(w, c.transform, 0);
                }
            }
            catch (Exception e) { w.WriteLine(label + " threw: " + e.Message); }
            if (n == 0) w.WriteLine("(no active " + label + ")");
            return n;
        }

        private static void Walk(StreamWriter w, Transform t, int depth)
        {
            var go = t.gameObject;
            var sb = new StringBuilder();
            sb.Append(' ', depth * 2).Append(go.activeSelf ? "+ " : "- ").Append(go.name);

            var comps = go.GetComponents<Component>();
            sb.Append("  [");
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] == null) continue;
                string tn = comps[i].GetIl2CppType().Name;
                if (tn == "Transform" || tn == "RectTransform" || tn == "CanvasRenderer") continue;
                sb.Append(tn).Append(' ');
            }
            sb.Append(']');

            var rt = t.TryCast<RectTransform>();
            if (rt != null)
                sb.Append("  pos=").Append(rt.anchoredPosition.ToString()).Append(" size=").Append(rt.sizeDelta.ToString());

            var tmp = go.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) sb.Append("  text=\"").Append(tmp.text.Replace("\n", "\\n")).Append('"');

            w.WriteLine(sb.ToString());
            if (depth >= MaxDepth) return;
            for (int i = 0; i < t.childCount; i++) Walk(w, t.GetChild(i), depth + 1);
        }
    }
}
