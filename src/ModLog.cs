using System;
using MelonLoader;
using UnityEngine;

namespace BASaveGame
{
    /// <summary>
    /// Logging split between players and developers: players get one line per save/load plus real
    /// problems; the step-by-step detail only appears with DeveloperMode on.
    /// </summary>
    internal static class ModLog
    {
        internal static void Info(string s) => MelonLogger.Msg(s);
        internal static void Warn(string s) => MelonLogger.Warning(s);
        internal static void Error(string s) => MelonLogger.Error(s);
        internal static void Dev(string s) { if (SaveMod.DevMode) MelonLogger.Msg(s); }
        internal static void DevWarn(string s) { if (SaveMod.DevMode) MelonLogger.Warning(s); }
    }

    /// <summary>
    /// Player-facing messages: a short on-screen banner (IMGUI, drawn from SaveMod.OnGUI) plus one
    /// log line. Used for save/load results and for anything that stops a save or load, so the
    /// player never has to read the log to find out what happened.
    /// </summary>
    internal static class Notify
    {
        private static string _text;
        private static bool _error;
        private static float _until;
        private static bool _broken;
        private static GUIStyle _style;
        private static float _styleScale;

        /// <summary>Banner <paramref name="banner"/>; log <paramref name="log"/> (or the banner text).</summary>
        internal static void Info(string banner, string log = null)
        {
            ModLog.Info(log ?? banner);
            Show(banner, false);
        }

        internal static void Error(string banner, string log = null)
        {
            ModLog.Warn(log ?? banner);
            Show(banner, true);
        }

        /// <summary>Banner only, no log line (prompts like "click again").</summary>
        internal static void Hint(string banner) => Show(banner, false);

        private static void Show(string text, bool error)
        {
            _text = text;
            _error = error;
            _until = Time.realtimeSinceStartup + (error ? 6f : 3.5f);
        }

        internal static void Draw()
        {
            if (_broken || _text == null) return;
            if (Time.realtimeSinceStartup > _until) { _text = null; return; }
            try
            {
                // IMGUI draws in raw pixels, so size everything for 1080p and scale to the screen.
                float scale = Mathf.Max(1f, Screen.height / 1080f);
                if (_style == null || _styleScale != scale)
                {
                    _style = new GUIStyle(GUI.skin.box);
                    _style.fontSize = Mathf.RoundToInt(17 * scale);
                    _style.fontStyle = FontStyle.Bold;
                    _style.alignment = TextAnchor.MiddleCenter;
                    _style.wordWrap = true;
                    _styleScale = scale;
                }
                float w = Mathf.Min(600f * scale, Screen.width - 40f);
                var rect = new Rect((Screen.width - w) / 2f, 72f * scale, w, 58f * scale);
                Color old = GUI.color;
                GUI.color = _error ? new Color(1f, 0.55f, 0.5f) : Color.white;
                GUI.Box(rect, "", _style);   // three times for a darker backing behind the text
                GUI.Box(rect, "", _style);
                GUI.Box(rect, _text, _style);
                GUI.color = old;
            }
            catch (Exception e)
            {
                _broken = true;
                ModLog.Warn("[ui] on-screen messages unavailable (" + e.Message + "); messages go to the log only.");
            }
        }
    }
}
