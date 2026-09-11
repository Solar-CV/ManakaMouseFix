using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ManakaMouseFix;

[BepInPlugin("com.lolo.manaka.mousefix", "Manaka Mouse Diagnostic", "0.14.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    /// <summary>Re-apply the resolution once shortly after startup, to repair a
    /// desynced render viewport (the "only the top-left corner renders / rest is
    /// black" failure). Off by default.</summary>
    internal static ConfigEntry<bool> CfgFixOnStartup = null!;

    /// <summary>Seconds after startup before the automatic repair runs.</summary>
    internal static ConfigEntry<float> CfgFixDelay = null!;

    /// <summary>Append the monitor's native resolution to the game's own resolution list,
    /// so the game can render at the screen's real aspect ratio (e.g. 2560x1600) instead
    /// of letterboxing everything to 16:9. Off by default.</summary>
    internal static ConfigEntry<bool> CfgInjectNative = null!;

    /// <summary>Automatically force the monitor's native resolution whenever the game
    /// sits in a fullscreen mode at a non-native size. The game keeps re-applying its
    /// own 16:9 resolution (2560x1440), which letterboxes the picture (80px bars at the
    /// top and bottom) and shifts the mouse on a 16:10 2560x1600 panel. On by default.</summary>
    internal static ConfigEntry<bool> CfgGuard = null!;

    public override void Load()
    {
        Logger = Log;

        CfgFixOnStartup = Config.Bind(
            "Repair", "ReapplyResolutionOnStartup", false,
            "If true, the plugin re-applies the current resolution a few seconds after " +
            "startup. This repairs the 'game renders only the top-left corner of the " +
            "window and everything else is black' state, which happens when the game " +
            "tries to restore an unsupported fullscreen mode and gives up.");

        CfgFixDelay = Config.Bind(
            "Repair", "ReapplyDelaySeconds", 6f,
            "Seconds to wait after startup before the automatic repair runs. " +
            "Give the game time to finish loading first.");

        CfgInjectNative = Config.Bind(
            "Repair", "AddNativeResolutionToGameList", false,
            "If true, the plugin appends your monitor's native resolution (e.g. 2560x1600) to " +
            "the game's own resolution list at startup, when it is not listed yet. This lets " +
            "the game render at the screen's true aspect ratio (16:10) instead of letterboxing " +
            "everything to 16:9. (Note: the game's settings UI does not read this list, so " +
            "the added entry may not show up there. Kept for experiments only.)");

        CfgGuard = Config.Bind(
            "Repair", "AutoGuardFullscreenResolution", true,
            "If true, whenever the game is fullscreen at a resolution other than the monitor's " +
            "native one, the plugin forces the native resolution back (usually within ~0.5 s). " +
            "This game re-applies its own 16:9 setting (e.g. 2560x1440) every time it enters " +
            "fullscreen or graphics settings are applied; on a 16:10 2560x1600 panel that " +
            "leaves 80px black bars top and bottom and shifts the mouse pointer. " +
            "Set to false to turn the guard off (F11 still works manually).");

        Log.LogInfo("========================================================");
        Log.LogInfo("Manaka Mouse Diagnostic 0.14.0");
        Log.LogInfo("  AUTO = fullscreen guard: any non-native fullscreen resolution is forced");
        Log.LogInfo("         back to native within ~0.5 s (kills the 16:9 black bars)");
        Log.LogInfo("  F11 = FORCE native resolution now  (manual version of AUTO)");
        Log.LogInfo("  F8  = SAFE full dump (never touches game internals)");
        Log.LogInfo("  F9  = ghost cursor + MAGENTA/ORANGE boxes showing where Unity");
        Log.LogInfo("        believes the biggest UI panels actually are on screen");
        Log.LogInfo("  F10 = PANIC: force Cursor.lockState=None / visible=true");
        Log.LogInfo("  F7  = probe WindowModeOptionApplyer (verified safe)");
        Log.LogInfo("  F6  = probe InputManager (verified safe)");
        Log.LogInfo("  cfg = BepInEx/config/com.lolo.manaka.mousefix.cfg");
        Log.LogInfo("  NOTE: touching CursorController freezes the cursor - never done");
        Log.LogInfo("========================================================");

        AddComponent<MouseDiagnostic>();
    }
}

/// <summary>
/// The only MonoBehaviour in the plugin. Everything that needs to be marshalled
/// into the IL2CPP domain lives here and takes no exotic parameters.
/// All heavy lifting is delegated to Report, which is a plain static class and
/// therefore never touched by Il2CppInterop's type injection.
/// </summary>
public class MouseDiagnostic : MonoBehaviour
{
    private float _nextTick;
    private float _nextDump;
    private readonly int[] _earlyFrames = { 1, 5, 15, 30, 60, 120, 300 };
    private int _earlyIndex;

    private bool _overlay;
    private string _lastSignature = "(none)";
    private bool _autoFixDone;

    // cursor-state watchdog
    private bool _cursorInit;
    private CursorLockMode _lastLock;
    private bool _lastVisible;

    // resolution-change burst capture
    private int _lsw = -1, _lsh = -1;
    private FullScreenMode _lfm;
    private float _burstUntil;
    private float _burstNext;
    private int _burstCount;

    public void Update()
    {
        // --- resolution-change burst capture (catches TRANSIENT wrong states) ---
        try { BurstWatch(); }
        catch (Exception e) { Plugin.Logger.LogError("MM| burst error: " + e); }

        // --- AUTO: force native resolution back whenever fullscreen is not native ---
        try { GuardWatch(); }
        catch (Exception e) { Plugin.Logger.LogError("MM| guard error: " + e); }

        // --- hotkeys ---
        try
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f8Key.wasPressedThisFrame)
                    Dump("F8-manual");

                if (kb.f7Key.wasPressedThisFrame)
                    Probe("WindowModeOptionApplyer");

                if (kb.f6Key.wasPressedThisFrame)
                    Probe("InputManager");

                if (kb.f9Key.wasPressedThisFrame)
                {
                    _overlay = !_overlay;
                    Plugin.Logger.LogInfo("MM| overlay = " + (_overlay ? "ON" : "OFF"));
                }

                // F10 = panic button: give the human their mouse back
                if (kb.f10Key.wasPressedThisFrame)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                    Plugin.Logger.LogWarning("MM| F10 PANIC -> forced Cursor.lockState=None, Cursor.visible=true");
                }

                // F11 = force Unity to rebuild the swap chain + viewport.
                //      Equivalent to what a successful Alt+Enter does, but callable on
                //      demand. Use it when the game renders only the top-left corner
                //      of the window and leaves the rest black.
                if (kb.f11Key.wasPressedThisFrame)
                    Report.ForceNativeResolution("F11 requested by user");

            }
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| hotkey error: " + e); }

        // --- optional automatic repairs, shortly after startup ---
        try
        {
            if (!_autoFixDone && Time.realtimeSinceStartup >= Plugin.CfgFixDelay.Value)
            {
                if (Plugin.CfgFixOnStartup.Value)
                {
                    if (Report.ViewportLooksWrong())
                        Report.ReapplyResolution("startup auto-fix (viewport/window mismatch detected)");
                    else
                        Plugin.Logger.LogInfo("MM| startup auto-fix: no mismatch detected, nothing to do");
                }

                if (Plugin.CfgInjectNative.Value)
                    Report.InjectNativeResolution();

                _autoFixDone = true;
            }
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| autofix error: " + e); }

        // --- early-frame dumps: catches initialisation-order problems ---
        try
        {
            while (_earlyIndex < _earlyFrames.Length && Time.frameCount >= _earlyFrames[_earlyIndex])
            {
                Dump("early-frame-" + _earlyFrames[_earlyIndex]);
                _earlyIndex++;
            }
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| early dump error: " + e); }

        // --- compact tick every 2 s ---
        if (Time.unscaledTime >= _nextTick)
        {
            _nextTick = Time.unscaledTime + 2f;
            try { Tick(); }
            catch (Exception e) { Plugin.Logger.LogError("MM| tick error: " + e); }
        }

        // --- full dump every 30 s ---
        if (Time.unscaledTime >= _nextDump)
        {
            _nextDump = Time.unscaledTime + 30f;
            try { Dump("periodic"); }
            catch (Exception e) { Plugin.Logger.LogError("MM| dump error: " + e); }
        }
    }

    /// <summary>
    /// A resolution/window-mode change often mis-renders for a moment before the game
    /// settles. Waiting 3 s before pressing F8 would miss it entirely, so we sample
    /// densely for 4 s after every change and force full dumps along the way.
    /// </summary>
    private void BurstWatch()
    {
        int sw = Screen.width, sh = Screen.height;
        FullScreenMode fm = Screen.fullScreenMode;

        if (sw != _lsw || sh != _lsh || fm != _lfm)
        {
            Plugin.Logger.LogWarning("MM| BURST START  " + _lsw + "x" + _lsh + " " + _lfm
                + "  ==>  " + sw + "x" + sh + " " + fm);
            _lsw = sw; _lsh = sh; _lfm = fm;
            _burstUntil = Time.realtimeSinceStartup + 4f;
            _burstNext = 0f;
            _burstCount = 0;

            // if the game just landed on a non-native fullscreen size, correct it right
            // away instead of waiting for the next 0.5 s poll
            GuardMaybeFix();
        }

        if (Time.realtimeSinceStartup < _burstUntil && Time.realtimeSinceStartup >= _burstNext)
        {
            _burstNext = Time.realtimeSinceStartup + 0.08f;
            _burstCount++;
            Plugin.Logger.LogInfo("MM| " + Report.BurstLine(_burstCount));
            if (_burstCount % 6 == 0)
                Dump("burst-" + _burstCount);
        }
    }

    // =====================================================================
    //  AUTO: fullscreen native-resolution guard (0.14.0)
    // =====================================================================
    private float _nextGuardCheck;
    private float _guardCooldownUntil;
    private float _guardWindowStart;
    private int _guardFixes;
    private float _guardSuspendedUntil;
    private bool _guardBannerLogged;
    private bool _guardNativeFailLogged;

    /// <summary>Runs every frame; throttles itself to one check every 0.5 s.</summary>
    private void GuardWatch()
    {
        if (!Plugin.CfgGuard.Value) return;
        if (Time.realtimeSinceStartup < _nextGuardCheck) return;
        _nextGuardCheck = Time.realtimeSinceStartup + 0.5f;
        GuardMaybeFix();
    }

    /// <summary>
    /// The game re-applies its own 16:9 resolution (e.g. 2560x1440) every time it enters
    /// fullscreen or graphics settings are applied. On a 16:10 2560x1600 panel that
    /// letterboxes the picture (80px black bars top and bottom) and shifts the mouse.
    /// This detects exactly that state and forces the native resolution back.
    /// </summary>
    private void GuardMaybeFix()
    {
        try
        {
            if (!Plugin.CfgGuard.Value) return;

            float now = Time.realtimeSinceStartup;
            if (now < 5f) return;                  // let the game finish booting first
            if (now < _guardSuspendedUntil) return;

            if (!_guardBannerLogged)
            {
                _guardBannerLogged = true;
                Plugin.Logger.LogInfo("MM| GUARD armed: fullscreen && Screen != native -> force native (checked every 0.5 s)");
            }

            var mode = Screen.fullScreenMode;
            if (mode != FullScreenMode.FullScreenWindow && mode != FullScreenMode.ExclusiveFullScreen)
                return;                            // windowed / maximized: nothing to do

            int nw = 0, nh = 0;
            try { nw = Display.main.systemWidth; nh = Display.main.systemHeight; }
            catch (Exception e)
            {
                if (!_guardNativeFailLogged)
                {
                    _guardNativeFailLogged = true;
                    Plugin.Logger.LogError("MM| GUARD off: cannot read Display.main system size: " + e.GetType().Name);
                }
                return;
            }
            if (nw <= 0 || nh <= 0) return;

            int sw = Screen.width, sh = Screen.height;
            if (sw <= 0 || sh <= 0) return;
            if (sw == nw && sh == nh) return;      // already native - nothing to do

            if (now < _guardCooldownUntil) return;

            if (now - _guardWindowStart > 60f) { _guardWindowStart = now; _guardFixes = 0; }
            _guardFixes++;
            if (_guardFixes > 5)
            {
                _guardSuspendedUntil = now + 60f;
                Plugin.Logger.LogWarning("MM| GUARD: 5 corrections within 60 s, suspending for 60 s to avoid fighting the game");
                return;
            }

            Plugin.Logger.LogWarning("MM| GUARD: fullscreen " + sw + "x" + sh + " " + mode
                + " != native " + nw + "x" + nh + " -> forcing native");
            Report.ForceNativeResolution("auto-guard");
            _guardCooldownUntil = now + 2f;
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| GUARD error: " + e); }
    }

    private void Tick()
    {
        Plugin.Logger.LogInfo("MM| " + Report.TickLine());

        string sig = Report.Signature();
        if (sig != _lastSignature)
        {
            string prev = _lastSignature;
            _lastSignature = sig;
            if (prev != "(none)" && prev != "boot")
                Plugin.Logger.LogWarning("MM| !!! SIGNATURE CHANGED: " + prev + "  ==>  " + sig);

            // A fullscreen/window switch can re-create or re-style the window, so drop
            // the cached handle. Otherwise the Win32 measurement (green cross) keeps
            // using the previous window's geometry and drifts away from the truth.
            Report.InvalidateWindow();

            Dump("changed");
        }

        if (Report.HasSizeMismatch(out string mismatch))
            Plugin.Logger.LogWarning("MM| !!! SIZE MISMATCH: " + mismatch);
    }

    private void Dump(string reason) => EmitLines(Report.Build(reason));

    /// <summary>
    /// Probe ONE group of game-internal statics, then immediately report the cursor
    /// state. If the cursor freezes right after a probe, that probe is the culprit.
    /// </summary>
    private void Probe(string what)
    {
        Plugin.Logger.LogWarning("MM| PROBE-START [" + what + "]  Cursor.lockState=" + Cursor.lockState + " visible=" + Cursor.visible);
        try
        {
            switch (what)
            {
                case "WindowModeOptionApplyer": Report.ProbeWindowMode(); break;
                case "InputManager": Report.ProbeInputManager(); break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("MM| PROBE THREW [" + what + "]: " + e);
        }
        Plugin.Logger.LogWarning("MM| PROBE-DONE  [" + what + "]  Cursor.lockState=" + Cursor.lockState + " visible=" + Cursor.visible);
    }

    /// <summary>
    /// Frame-accurate watchdog for whoever is messing with the cursor.
    /// Runs in LateUpdate so it sees the state AFTER the game's own Update.
    /// </summary>
    public void LateUpdate()
    {
        try
        {
            var ls = Cursor.lockState;
            var vis = Cursor.visible;

            if (!_cursorInit)
            {
                _cursorInit = true;
                _lastLock = ls;
                _lastVisible = vis;
                Plugin.Logger.LogInfo("MM| CURSOR WATCH armed: lockState=" + ls + " visible=" + vis
                    + " Screen=" + Screen.width + "x" + Screen.height
                    + " client=" + Report.GameClientSizeString());
                return;
            }

            if (ls != _lastLock || vis != _lastVisible)
            {
                Plugin.Logger.LogWarning("MM| CURSOR STATE CHANGE frame=" + Time.frameCount
                    + " t=" + Time.realtimeSinceStartup.ToString("F3")
                    + "  lockState: " + _lastLock + " -> " + ls
                    + "   visible: " + _lastVisible + " -> " + vis
                    + "   Screen=" + Screen.width + "x" + Screen.height
                    + "  client=" + Report.GameClientSizeString());
                _lastLock = ls;
                _lastVisible = vis;
            }
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| cursor watch error: " + e); }
    }

    /// <summary>Write one log entry per physical line so every line is greppable.</summary>
    private static void EmitLines(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            Plugin.Logger.LogInfo("MM| " + line);
        }
    }

    /// <summary>Draw the outline of a rect using four thin quads (IMGUI has no border).</summary>
    private static void DrawBox(Rect r)
    {
        var t = Texture2D.whiteTexture;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, 2), t);
        GUI.DrawTexture(new Rect(r.x, r.y + r.height - 2, r.width, 2), t);
        GUI.DrawTexture(new Rect(r.x, r.y, 2, r.height), t);
        GUI.DrawTexture(new Rect(r.x + r.width - 2, r.y, 2, r.height), t);
    }

    // =====================================================================
    //  On-screen ghost cursor (F9)
    // =====================================================================
    public void OnGUI()
    {
        if (!_overlay) return;
        try
        {
            var white = Texture2D.whiteTexture;

            // RED = Unity's belief (new Input System position), converted to top-left origin
            var m = Mouse.current;
            if (m != null)
            {
                var p = m.position.ReadValue();
                float x = p.x, y = Screen.height - p.y;
                GUI.color = Color.red;
                GUI.DrawTexture(new Rect(x - 14, y - 1, 28, 2), white);
                GUI.DrawTexture(new Rect(x - 1, y - 14, 2, 28), white);
                GUI.color = Color.yellow;
                GUI.Label(new Rect(x + 18, y - 11, 900, 22), "<- UNITY thinks pointer is here");
                GUI.color = Color.white;
            }

            // GREEN = Win32 truth
            IntPtr hwnd = Report.GameWindow();
            RECT cr;
            bool hasRect = Win32.GetClientRect(hwnd, out cr);
            POINT p2;
            if (Win32.GetCursorPos(out p2))
            {
                var cp = p2;
                Win32.ScreenToClient(hwnd, ref cp);
                float x = cp.X, y = cp.Y;
                GUI.color = Color.green;
                GUI.DrawTexture(new Rect(x - 14, y - 1, 28, 2), white);
                GUI.DrawTexture(new Rect(x - 1, y - 14, 2, 28), white);
                GUI.color = Color.yellow;
                GUI.Label(new Rect(x + 18, y + 4, 900, 22), "<- WIN32 says pointer is here");
                GUI.color = Color.white;
            }

            // --- UI panel rectangles: does the game's LAYOUT match what you SEE? ---
            try
            {
                var rects = Report.OverlayProbe();
                int idx = 0;
                foreach (var r in rects)
                {
                    float x0 = r.L;
                    float x1 = r.L + r.W;
                    float y0 = Screen.height - (r.B + r.H);   // to IMGUI top-left origin
                    float y1 = Screen.height - r.B;
                    GUI.color = idx == 0 ? Color.magenta : new Color(1f, 0.5f, 0f);
                    DrawBox(new Rect(x0, y0, x1 - x0, y1 - y0));
                    GUI.color = Color.yellow;
                    GUI.Label(new Rect(x0 + 4, y0 + 4, 1200, 22),
                        "UI#" + idx + "  " + r.W.ToString("F0") + "x" + r.H.ToString("F0") + "  " + r.Path);
                    GUI.color = Color.white;
                    idx++;
                }
            }
            catch { }

            // corner readout
            string info = "Screen=" + Screen.width + "x" + Screen.height
                        + "  client=" + (hasRect ? (cr.Right - cr.Left) + "x" + (cr.Bottom - cr.Top) : "?")
                        + "  mode=" + Screen.fullScreenMode
                        + "  Cursor.visible=" + Cursor.visible;
            GUI.color = Color.yellow;
            GUI.Label(new Rect(12, 12, 1500, 24), "[ManakaMouseFix] " + info);
            GUI.color = Color.white;
        }
        catch { }
    }
}

// =========================================================================
//  Report builder - plain static class, never injected into IL2CPP
// =========================================================================
internal static class Report
{
    private static IntPtr _gameHwnd = IntPtr.Zero;

    /// <summary>
    /// The game's OWN window. Using GetForegroundWindow() is wrong: as soon as the
    /// user alt-tabs to anything (or Windows search pops up) we would measure that
    /// other window and report a bogus size mismatch.
    /// </summary>
    public static IntPtr GameWindow()
    {
        try
        {
            if (_gameHwnd != IntPtr.Zero && Win32.IsWindow(_gameHwnd) && IsUnityWindow(_gameHwnd)) return _gameHwnd;
        }
        catch { }
        _gameHwnd = IntPtr.Zero;

        // Unity's own window class. Process.MainWindowHandle is WRONG here: the
        // BepInEx console window belongs to this process too and usually wins,
        // which silently turned every size measurement into console dimensions.
        try
        {
            var h = Win32.FindWindow("UnityWndClass", null);
            if (h != IntPtr.Zero) { _gameHwnd = h; return h; }
        }
        catch { }

        try
        {
            using (var p = System.Diagnostics.Process.GetCurrentProcess())
            {
                p.Refresh();
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero && IsUnityWindow(h)) { _gameHwnd = h; return h; }
            }
        }
        catch { }

        return Win32.GetForegroundWindow();
    }

    private static bool IsUnityWindow(IntPtr h)
    {
        try
        {
            var sb = new StringBuilder(128);
            Win32.GetClassName(h, sb, sb.Capacity);
            return sb.ToString().StartsWith("UnityWndClass", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string GameWindowClass()
    {
        try
        {
            var sb = new StringBuilder(128);
            Win32.GetClassName(GameWindow(), sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return "?"; }
    }

    public static string GameClientSizeString()
    {
        try
        {
            RECT cr;
            if (Win32.GetClientRect(GameWindow(), out cr)) return (cr.Right - cr.Left) + "x" + (cr.Bottom - cr.Top);
        }
        catch { }
        return "?";
    }

    // ---------------------------------------------------------------- tick
    /// <summary>One dense line used during the post-change burst.</summary>
    public static string BurstLine(int n)
    {
        var sb = new StringBuilder();
        sb.Append("BURST ").Append(n).Append(" t=").Append(Time.realtimeSinceStartup.ToString("F2"));
        sb.Append(" Screen=").Append(Screen.width).Append('x').Append(Screen.height).Append(' ').Append(Screen.fullScreenMode);
        sb.Append(" client=").Append(GameClientSizeString());
        try
        {
            var c = Camera.main;
            if (c != null)
                sb.Append(" camPix=").Append(c.pixelRect.width.ToString("F0")).Append('x').Append(c.pixelRect.height.ToString("F0"))
                  .Append(" aspect=").Append(c.aspect.ToString("F5"));
            else sb.Append(" camPix=NULL");
        }
        catch { }
        try
        {
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (cv == null) continue;
                var rt = cv.GetComponent<RectTransform>();
                sb.Append(" |'").Append(cv.name).Append("'")
                  .Append(" rt=").Append(rt == null ? "?" : rt.rect.size.x.ToString("F0") + "x" + rt.rect.size.y.ToString("F0"))
                  .Append(" sf=").Append(cv.scaleFactor.ToString("F3"));
                var sc = cv.GetComponent<CanvasScaler>();
                if (sc != null)
                    sb.Append(" scaler=").Append(sc.scaleFactor.ToString("F3"))
                      .Append(" m=").Append(sc.matchWidthOrHeight.ToString("F2"));
            }
        }
        catch { }
        return sb.ToString();
    }
    public static string TickLine()
    {
        var sb = new StringBuilder();
        sb.Append("TICK t=").Append(Time.realtimeSinceStartup.ToString("F1"));
        sb.Append(" f=").Append(Time.frameCount);

        var m = Mouse.current;
        Vector2 unity = default;
        bool haveUnity = false;
        if (m != null)
        {
            unity = m.position.ReadValue();
            haveUnity = true;
            sb.Append(" unityMouse=(").Append(unity.x.ToString("F1")).Append(',').Append(unity.y.ToString("F1")).Append(')');
        }

        IntPtr hwnd = GameWindow();

        RECT cr;
        int clientW = -1, clientH = -1;
        if (Win32.GetClientRect(hwnd, out cr)) { clientW = cr.Right - cr.Left; clientH = cr.Bottom - cr.Top; }

        POINT p;
        if (Win32.GetCursorPos(out p))
        {
            var cp = p;
            Win32.ScreenToClient(hwnd, ref cp);
            sb.Append(" win32CursorClient=(").Append(cp.X).Append(',').Append(cp.Y).Append(')');
            sb.Append(" expectUnityMouse=(").Append(cp.X).Append(',').Append(clientH - cp.Y).Append(')');
            if (haveUnity)
            {
                sb.Append(" dU=(").Append((unity.x - cp.X).ToString("F1")).Append(',')
                  .Append((unity.y - (clientH - cp.Y)).ToString("F1")).Append(')');
            }
        }

        sb.Append(" Screen=").Append(Screen.width).Append('x').Append(Screen.height);
        sb.Append(" client=").Append(clientW).Append('x').Append(clientH);
        RECT wr;
        if (Win32.GetWindowRect(hwnd, out wr))
            sb.Append(" win=").Append(wr.Right - wr.Left).Append('x').Append(wr.Bottom - wr.Top);

        if (clientW > 0 && Screen.width > 0)
        {
            float rx = (float)Screen.width / clientW;
            float ry = (float)Screen.height / clientH;
            sb.Append(" ratio=(").Append(rx.ToString("F4")).Append(',').Append(ry.ToString("F4")).Append(')');
            if (Math.Abs(rx - 1f) > 0.01f || Math.Abs(ry - 1f) > 0.01f)
                sb.Append("  <<< MISMATCH");
        }

        return sb.ToString();
    }

    public static string Signature()
        => Screen.width + "x" + Screen.height + "|" + Screen.fullScreenMode + "|" + ClientSizeString();

    public static bool HasSizeMismatch(out string detail)
    {
        detail = "";
        try
        {
            IntPtr hwnd = GameWindow();
            RECT cr;
            if (!Win32.GetClientRect(hwnd, out cr)) return false;
            int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
            if (cw <= 0 || ch <= 0) return false;
            int dw = Screen.width - cw, dh = Screen.height - ch;
            if (Math.Abs(dw) <= 1 && Math.Abs(dh) <= 1) return false;
            detail = "Unity Screen=" + Screen.width + "x" + Screen.height
                   + " but Win32 client=" + cw + "x" + ch
                   + "  (delta " + dw + "," + dh + ")"
                   + "  ratio=" + ((float)Screen.width / cw).ToString("F4") + "," + ((float)Screen.height / ch).ToString("F4");
            return true;
        }
        catch { return false; }
    }

    private static string ClientSizeString()
    {
        try
        {
            RECT cr;
            if (Win32.GetClientRect(GameWindow(), out cr)) return (cr.Right - cr.Left) + "x" + (cr.Bottom - cr.Top);
        }
        catch { }
        return "?";
    }

    // -------------------------------------------------------------- full dump
    public static string Build(string reason)
    {
        var sb = new StringBuilder();
        sb.Append("DUMP-BEGIN [").Append(reason).Append(']').AppendLine();
        sb.Append("t=").Append(Time.realtimeSinceStartup.ToString("F3"))
          .Append(" unscaled=").Append(Time.unscaledTime.ToString("F3"))
          .Append(" frame=").Append(Time.frameCount)
          .Append(" timeScale=").Append(Time.timeScale.ToString("F2"))
          .Append(" focused=").Append(Application.isFocused)
          .Append(" platform=").Append(Application.platform)
          .AppendLine();

        Section(sb, "Screen", DumpScreen);
        Section(sb, "Cursor", DumpCursor);
        Section(sb, "Input", DumpInput);
        Section(sb, "Win32", DumpWin32);
        Section(sb, "EventSystem", DumpEventSystem);
        Section(sb, "Cameras", DumpCameras);
        Section(sb, "Canvases", DumpCanvases);
        sb.Append("-- Game state --  (NOT read here; use F5 / F6 / F7 probes if needed)").AppendLine();

        sb.Append("DUMP-END [").Append(reason).Append(']').AppendLine();
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string name, Action<StringBuilder> body)
    {
        try { body(sb); }
        catch (Exception e) { sb.Append("  !! ").Append(name).Append(" section failed: ").Append(e.Message).AppendLine(); }
    }

    private static void DumpScreen(StringBuilder sb)
    {
        sb.Append("-- Screen (what Unity believes) --").AppendLine();
        sb.Append("  Screen.width/height = ").Append(Screen.width).Append('x').Append(Screen.height);
        if (Screen.height > 0)
            sb.Append("   aspect=").Append(((float)Screen.width / Screen.height).ToString("F5"))
              .Append("   16:9? ").Append(Math.Abs((float)Screen.width / Screen.height - 16f / 9f) < 0.001f ? "YES" : "NO");
        sb.AppendLine();

        var r = Screen.currentResolution;
        sb.Append("  currentResolution = ").Append(r.width).Append('x').Append(r.height)
          .Append('@').Append(r.refreshRateRatio.value.ToString("F2")).Append("Hz").AppendLine();

        sb.Append("  Screen.dpi = ").Append(Screen.dpi.ToString("F1"))
          .Append("   fullScreen=").Append(Screen.fullScreen)
          .Append("   fullScreenMode=").Append(Screen.fullScreenMode).AppendLine();

        sb.Append("  Screen.safeArea = ").Append(Screen.safeArea.ToString()).AppendLine();
        sb.Append("  Screen.orientation = ").Append(Screen.orientation).AppendLine();

        sb.Append("  Display.main: system=").Append(Display.main.systemWidth).Append('x').Append(Display.main.systemHeight)
          .Append("  rendering=").Append(Display.main.renderingWidth).Append('x').Append(Display.main.renderingHeight)
          .Append("  active=").Append(Display.main.active)
          .Append("  displays=").Append(Display.displays.Length).AppendLine();

        sb.Append("  Screen.mainWindowPosition = ").Append(Screen.mainWindowPosition.ToString()).AppendLine();

        try
        {
            var res = Screen.resolutions;
            sb.Append("  Screen.resolutions (" ).Append(res.Length).Append(") = ");
            for (int i = 0; i < res.Length && i < 40; i++)
                sb.Append(res[i].width).Append('x').Append(res[i].height).Append(' ');
            sb.AppendLine();
        }
        catch (Exception e) { sb.Append("  Screen.resolutions failed: ").Append(e.Message).AppendLine(); }

        try
        {
            sb.Append("  PlayerPrefs: resW=").Append(PlayerPrefs.GetInt("Screenmanager Resolution Width", -1))
              .Append(" resH=").Append(PlayerPrefs.GetInt("Screenmanager Resolution Height", -1))
              .Append(" winW=").Append(PlayerPrefs.GetInt("Screenmanager Resolution Window Width", -1))
              .Append(" winH=").Append(PlayerPrefs.GetInt("Screenmanager Resolution Window Height", -1))
              .Append(" fsMode=").Append(PlayerPrefs.GetInt("Screenmanager Fullscreen mode", -1))
              .Append(" useNative=").Append(PlayerPrefs.GetInt("Screenmanager Resolution Use Native", -1))
              .AppendLine();
        }
        catch (Exception e) { sb.Append("  PlayerPrefs failed: ").Append(e.Message).AppendLine(); }
    }

    private static void DumpCursor(StringBuilder sb)
    {
        sb.Append("-- Cursor --").AppendLine();
        sb.Append("  Cursor.visible=").Append(Cursor.visible)
          .Append("   Cursor.lockState=").Append(Cursor.lockState).AppendLine();
    }

    private static void DumpInput(StringBuilder sb)
    {
        sb.Append("-- New Input System --").AppendLine();
        var m = Mouse.current;
        if (m == null)
        {
            sb.Append("  Mouse.current = NULL  (!! this alone can explain a broken pointer)").AppendLine();
        }
        else
        {
            var pos = m.position.ReadValue();
            var d = m.delta.ReadValue();
            var s = m.scroll.ReadValue();
            sb.Append("  position (origin BOTTOM-left) = (").Append(pos.x.ToString("F2")).Append(", ").Append(pos.y.ToString("F2")).Append(')').AppendLine();
            sb.Append("  position (origin TOP-left)    = (").Append(pos.x.ToString("F2")).Append(", ").Append((Screen.height - pos.y).ToString("F2")).Append(')').AppendLine();
            sb.Append("  delta=(").Append(d.x.ToString("F2")).Append(", ").Append(d.y.ToString("F2")).Append(')')
              .Append("  scroll=(").Append(s.x.ToString("F2")).Append(", ").Append(s.y.ToString("F2")).Append(')').AppendLine();
            sb.Append("  device=").Append(m.displayName)
              .Append("  enabled=").Append(m.enabled)
              .Append("  added=").Append(m.added).AppendLine();
        }

        sb.Append("-- Legacy Input (may be disabled at runtime) --").AppendLine();
        try
        {
            sb.Append("  Input.mousePosition=").Append(Input.mousePosition.ToString())
              .Append("  mousePresent=").Append(Input.mousePresent).AppendLine();
        }
        catch (Exception e) { sb.Append("  (legacy Input unavailable: ").Append(e.GetType().Name).Append(')').AppendLine(); }
    }

    private static void DumpWin32(StringBuilder sb)
    {
        sb.Append("-- Win32 (ground truth) --").AppendLine();
        IntPtr hwnd = GameWindow();
        sb.Append("  GAME window hwnd=").Append(hwnd).Append("  class='").Append(GameWindowClass()).Append('\'').AppendLine();
        sb.Append("  foreground window=").Append(Win32.GetForegroundWindow())
          .Append("  (differs from game window? ")
          .Append(Win32.GetForegroundWindow() != hwnd ? "YES - some other app is in front" : "no").Append(')').AppendLine();

        var title = new StringBuilder(256);
        Win32.GetWindowText(hwnd, title, title.Capacity);
        sb.Append("  title=\"").Append(title.ToString()).Append('"').AppendLine();

        RECT wr, cr;
        if (Win32.GetWindowRect(hwnd, out wr))
            sb.Append("  WindowRect=").Append(wr.Left).Append(',').Append(wr.Top).Append(" - ").Append(wr.Right).Append(',').Append(wr.Bottom)
              .Append("   size=").Append(wr.Right - wr.Left).Append('x').Append(wr.Bottom - wr.Top).AppendLine();

        int clientW = -1, clientH = -1;
        if (Win32.GetClientRect(hwnd, out cr))
        {
            clientW = cr.Right - cr.Left;
            clientH = cr.Bottom - cr.Top;
            sb.Append("  ClientRect size=").Append(clientW).Append('x').Append(clientH).AppendLine();
        }

        POINT p;
        if (Win32.GetCursorPos(out p))
        {
            sb.Append("  GetCursorPos (screen px, TOP-left) = (").Append(p.X).Append(", ").Append(p.Y).Append(')').AppendLine();
            var cp = p;
            Win32.ScreenToClient(hwnd, ref cp);
            sb.Append("  -> client px (TOP-left) = (").Append(cp.X).Append(", ").Append(cp.Y).Append(')').AppendLine();
            if (clientH > 0)
                sb.Append("  -> EXPECTED Unity mouse (BOTTOM-left) = (").Append(cp.X).Append(", ").Append(clientH - cp.Y).Append(')')
                  .Append("    <<< compare with the line above").AppendLine();
        }

        sb.Append("  GetSystemMetrics = ").Append(Win32.GetSystemMetrics(0)).Append('x').Append(Win32.GetSystemMetrics(1)).AppendLine();

        int style = Win32.GetWindowLong(hwnd, -16);
        int exStyle = Win32.GetWindowLong(hwnd, -20);
        sb.Append("  style=0x").Append(style.ToString("X8")).Append(" exStyle=0x").Append(exStyle.ToString("X8"))
          .Append("  hasCaption=").Append((style & 0x00C00000) != 0)
          .Append("  isPopup=").Append((style & unchecked((int)0x80000000)) != 0)
          .Append("  topmost=").Append((exStyle & 0x00000008) != 0)
          .Append("  maximized=").Append(Win32.IsZoomed(hwnd))
          .Append("  visible=").Append(Win32.IsWindowVisible(hwnd)).AppendLine();

        try
        {
            uint dpiWin = Win32.GetDpiForWindow(hwnd);
            int awareness = Win32.GetAwarenessFromDpiAwarenessContext(Win32.GetThreadDpiAwarenessContext());
            string aw = awareness == 0 ? "UNAWARE(0)" : awareness == 1 ? "SYSTEM_AWARE(1)" : awareness == 2 ? "PER_MONITOR_AWARE(2)" : awareness.ToString();
            sb.Append("  DPI: GetDpiForWindow=").Append(dpiWin).Append("  systemDpi=").Append(Win32.GetDpiForSystem())
              .Append("  scale=").Append((dpiWin / 96f * 100f).ToString("F0")).Append('%')
              .Append("  processAwareness=").Append(aw).AppendLine();
        }
        catch (Exception e) { sb.Append("  -- DPI query unavailable: ").Append(e.GetType().Name).AppendLine(); }
    }

    private static void DumpEventSystem(StringBuilder sb)
    {
        sb.Append("-- EventSystem (UI hit-testing) --").AppendLine();
        var es = EventSystem.current;
        if (es == null)
        {
            sb.Append("  EventSystem.current = NULL").AppendLine();
            return;
        }

        sb.Append("  EventSystem on '").Append(es.gameObject.name).Append("'")
          .Append("  activeInHierarchy=").Append(es.gameObject.activeInHierarchy)
          .Append("  enabled=").Append(es.enabled).AppendLine();

        sb.Append("  IsPointerOverGameObject() = ").Append(es.IsPointerOverGameObject()).AppendLine();

        var im = es.currentInputModule;
        if (im == null)
        {
            sb.Append("  currentInputModule = NULL  (!! no module means no UI clicks at all)").AppendLine();
        }
        else
        {
            sb.Append("  currentInputModule = ").Append(SafeTypeName(im)).AppendLine();
            sb.Append("  module enabled=").Append(im.enabled).Append("  active=").Append(im.gameObject.activeInHierarchy).AppendLine();
        }

        var m = Mouse.current;
        if (m != null)
        {
            var pos = m.position.ReadValue();
            var ped = new PointerEventData(es);
            ped.position = pos;
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ped, hits);
            sb.Append("  RaycastAll at (").Append(pos.x.ToString("F1")).Append(',').Append(pos.y.ToString("F1"))
              .Append(") -> ").Append(hits.Count).Append(" UI element(s) under the pointer").AppendLine();
            int max = hits.Count < 5 ? hits.Count : 5;
            for (int i = 0; i < max; i++)
            {
                var hit = hits[i];
                sb.Append("      #").Append(i).Append(' ').Append(PathOf(hit.gameObject.transform)).AppendLine();
                try
                {
                    var rt = hit.gameObject.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        var canvas = hit.gameObject.GetComponentInParent<Canvas>();
                        Camera? cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                        Vector2 screenPt = RectTransformUtility.WorldToScreenPoint(cam, rt.position);
                        sb.Append("         worldPos=").Append(rt.position.ToString())
                          .Append("  screenPos=").Append(screenPt.ToString())
                          .Append("  sizeDelta=").Append(rt.sizeDelta.ToString()).AppendLine();
                    }
                }
                catch (Exception ie) { sb.Append("         (rect info failed: ").Append(ie.Message).Append(')').AppendLine(); }
            }
        }
    }

    private static void DumpCameras(StringBuilder sb)
    {
        sb.Append("-- Cameras --").AppendLine();
        sb.Append("  Camera.main = ").Append(Camera.main == null ? "NULL" : "'" + Camera.main.name + "'")
          .AppendLine();

        var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
        sb.Append("  active Camera count = ").Append(cams.Length).AppendLine();
        for (int i = 0; i < cams.Length && i < 8; i++)
        {
            var c = cams[i];
            if (c == null) continue;
            sb.Append("    '").Append(c.name).Append("' rect=").Append(c.rect.ToString())
              .Append(" pixelRect=").Append(c.pixelRect.ToString())
              .Append(" aspect=").Append(c.aspect.ToString("F5"))
              .Append(" depth=").Append(c.depth)
              .Append(" targetTexture=").Append(c.targetTexture == null ? "null" : c.targetTexture.name).AppendLine();

            // The projection matrix is the last place a 16:9 assumption can hide.
            // For a perspective camera m00 = 1/(aspect*tan(fov/2)) and
            // m11 = 1/tan(fov/2), so m11/m00 is the aspect the camera really renders with.
            try
            {
                var p = c.projectionMatrix;
                float impliedAspect = (Math.Abs(p.m00) > 1e-6f) ? p.m11 / p.m00 : 0f;
                sb.Append("        fov=").Append(c.fieldOfView.ToString("F4"))
                  .Append(" orthographic=").Append(c.orthographic)
                  .Append(" m00=").Append(p.m00.ToString("F6"))
                  .Append(" m11=").Append(p.m11.ToString("F6"))
                  .Append(" impliedAspect=").Append(impliedAspect.ToString("F6")).AppendLine();
                if (impliedAspect > 0f && Math.Abs(impliedAspect - c.aspect) > 0.01f)
                    sb.Append("        *** PROJECTION ASPECT MISMATCH: camera.aspect=")
                      .Append(c.aspect.ToString("F4")).Append(" but the projection implies ")
                      .Append(impliedAspect.ToString("F4")).AppendLine();
            }
            catch (Exception e) { sb.Append("        projection read failed: ").Append(e.Message).AppendLine(); }

            // If the bars are the camera's own background colour, that tells us the
            // camera DOES cover those rows and only its geometry misses them.
            try
            {
                sb.Append("        clearFlags=").Append(c.clearFlags)
                  .Append(" backgroundColor=").Append(c.backgroundColor.ToString()).AppendLine();
            }
            catch (Exception e) { sb.Append("        clearFlags read failed: ").Append(e.Message).AppendLine(); }
        }
    }

    private static void DumpCanvases(StringBuilder sb)
    {
        sb.Append("-- Canvases --").AppendLine();
        var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
        sb.Append("  active Canvas count = ").Append(canvases.Length).AppendLine();

        int shown = 0;
        foreach (var c in canvases)
        {
            if (c == null) continue;
            if (shown >= 6) { sb.Append("    ... more canvases skipped").AppendLine(); break; }
            shown++;

            sb.Append("    '").Append(PathOf(c.transform)).Append("'")
              .Append("  renderMode=").Append(c.renderMode)
              .Append("  sortingOrder=").Append(c.sortingOrder)
              .Append("  scaleFactor=").Append(c.scaleFactor.ToString("F4"))
              .Append("  isRoot=").Append(c.isRootCanvas)
              .AppendLine();
            try
            {
                sb.Append("        pixelRect=").Append(c.pixelRect.ToString())
                  .Append("  renderingDisplaySize=").Append(c.renderingDisplaySize.ToString()).AppendLine();
            }
            catch { }

            try
            {
                var rt = c.GetComponent<RectTransform>();
                sb.Append("        canvasRt.rect.size=").Append(rt == null ? "?" : rt.rect.size.ToString())
                  .Append("  lossyScale=").Append(rt == null ? "?" : rt.lossyScale.ToString())
                  .AppendLine();
            }
            catch { }

            try
            {
                var cam = c.worldCamera;
                sb.Append("        worldCamera=").Append(cam == null ? "null" : "'" + cam.name + "'")
                  .Append(cam == null ? "" : "  rect=" + cam.rect.ToString() + "  pixelRect=" + cam.pixelRect.ToString())
                  .AppendLine();
            }
            catch { }

            try
            {
                var scaler = c.GetComponent<CanvasScaler>();
                if (scaler != null)
                    sb.Append("        CanvasScaler: uiScaleMode=").Append(scaler.uiScaleMode)
                      .Append("  referenceResolution=").Append(scaler.referenceResolution.ToString())
                      .Append("  screenMatchMode=").Append(scaler.screenMatchMode)
                      .Append("  matchWidthOrHeight=").Append(scaler.matchWidthOrHeight.ToString("F3"))
                      .Append("  scaleFactor=").Append(scaler.scaleFactor.ToString("F4"))
                      .AppendLine();
            }
            catch { }

            try { DumpBigUiRects(sb, c); }
            catch (Exception e) { sb.Append("        bbox failed: ").Append(e.Message).AppendLine(); }
        }
    }

    internal sealed class UiRect
    {
        public string Path = "";
        public float L, B, W, H;          // screen px, origin BOTTOM-left
        public float Area => W * H;
    }

    /// <summary>
    /// Physical (screen pixel) rect of the biggest UI elements under a canvas.
    /// Uses rt.pivot so the box is correct even for corner-anchored elements.
    /// </summary>
    private static void CollectUiRects(List<UiRect> list, Canvas c, int limit)
    {
        bool overlay = c.renderMode == RenderMode.ScreenSpaceOverlay;
        Camera? cam = overlay ? null : c.worldCamera;

        var stack = new System.Collections.Generic.Stack<Transform>();
        stack.Push(c.transform);
        int visited = 0;

        while (stack.Count > 0 && visited < limit)
        {
            var t = stack.Pop();
            visited++;
            try { for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i)); }
            catch { }

            try
            {
                var rt = t.GetComponent<RectTransform>();
                if (rt == null) continue;
                var sz = rt.rect.size;
                var s = rt.lossyScale;
                float wx = sz.x * s.x;
                float wy = sz.y * s.y;
                if (Math.Abs(wx * wy) < 1f) continue;

                Vector2 pt = overlay
                    ? new Vector2(rt.position.x, rt.position.y)
                    : RectTransformUtility.WorldToScreenPoint(cam, rt.position);

                // position is the PIVOT, not the centre
                Vector2 pv = rt.pivot;
                float left = pt.x - pv.x * wx;
                float bottom = pt.y - pv.y * wy;
                float w = Math.Abs(wx);
                float h = Math.Abs(wy);
                if (wx < 0) left -= w;
                if (wy < 0) bottom -= h;

                list.Add(new UiRect { Path = PathOf(t), L = left, B = bottom, W = w, H = h });
            }
            catch { }
        }
    }

    private static void DumpBigUiRects(StringBuilder sb, Canvas c)
    {
        var list = new List<UiRect>();
        CollectUiRects(list, c, 500);
        list.Sort((a, b) => b.Area.CompareTo(a.Area));

        sb.Append("        largest UI rects (screen px, origin BOTTOM-left), screen=")
          .Append(Screen.width).Append('x').Append(Screen.height).AppendLine();
        int n = 0;
        foreach (var e in list)
        {
            if (n++ >= 8) break;
            sb.Append("          ")
              .Append(e.W.ToString("F0")).Append('x').Append(e.H.ToString("F0"))
              .Append("  x[").Append(e.L.ToString("F0")).Append("..").Append((e.L + e.W).ToString("F0")).Append(']')
              .Append("  y[").Append(e.B.ToString("F0")).Append("..").Append((e.B + e.H).ToString("F0")).Append(']')
              .Append("  ").Append(e.Path)
              .AppendLine();
        }
    }

    /// <summary>
    /// Biggest UI element of the Overlay canvases (excluding the canvas root itself).
    /// Used by the F9 overlay to draw a rectangle over where Unity believes the
    /// game's panel actually is - if that box does not match what you SEE, we have
    /// proof that layout and rendering disagree.
    /// </summary>
    public static List<UiRect> OverlayProbe()
    {
        var list = new List<UiRect>();
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
        {
            if (c == null || c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            var sub = new List<UiRect>();
            CollectUiRects(sub, c, 400);
            float canvasArea = 0;
            foreach (var e in sub)
            {
                if (e.Path == PathOf(c.transform)) { canvasArea = e.Area; continue; }
                list.Add(e);
            }
            // ignore the full-screen canvas itself
            if (canvasArea <= 0) { }
        }
        list.Sort((a, b) => b.Area.CompareTo(a.Area));
        if (list.Count > 3) list.RemoveRange(3, list.Count - 3);
        return list;
    }

    // ------------------------------------------------------- game internals
    //  Probes log IMMEDIATELY after every single read, so if the cursor freezes
    //  the log shows exactly which member was being touched at that moment.
    private static void P(string s) => Plugin.Logger.LogInfo("MM| PROBE " + s);

    /// <summary>Run one probe step; the log shows how far we got even if it freezes.</summary>
    private static void Step(string label, Func<object?> body)
    {
        P(label + " ...");
        try
        {
            var v = body();
            P(label + " OK -> " + (v == null ? "null" : v.ToString()));
        }
        catch (Exception e)
        {
            P(label + " THREW " + e.GetType().Name + ": " + e.Message);
        }
    }

    public static void ProbeWindowMode()
    {
        const string N = "ExposureUnnoticed2.Scripts.Base.WindowModeOptionApplyer.";

        Step("A1 " + N + "initialized",
            () => ExposureUnnoticed2.Scripts.Base.WindowModeOptionApplyer.initialized);

        Step("A2 " + N + "ResolutionDataList",
            () => Fmt(ExposureUnnoticed2.Scripts.Base.WindowModeOptionApplyer.ResolutionDataList));

        Step("A3 " + N + "WideResolutionDataList",
            () => Fmt(ExposureUnnoticed2.Scripts.Base.WindowModeOptionApplyer.WideResolutionDataList));

        Step("A4 " + N + "GetTargetResolutionList()",
            () => Fmt(ExposureUnnoticed2.Scripts.Base.WindowModeOptionApplyer.GetTargetResolutionList()));
    }

    private static string Fmt(Il2CppSystem.Collections.Generic.List<ExposureUnnoticed2.Scripts.Base.ResolutionData>? list)
    {
        if (list == null) return "NULL";
        var s = list.Count + " entries:";
        for (int i = 0; i < list.Count; i++)
            s += "  [" + i + "] " + list[i].X + "x" + list[i].Y + " '" + list[i].Name + "'";
        return s;
    }

    public static void ProbeInputManager()
    {
        const string N = "ExposureUnnoticed2.Scripts.Base.InputManager.";

        Step("B1 " + N + "Instance",
            () => ExposureUnnoticed2.Scripts.Base.InputManager.Instance == null ? "NULL" : "object present");

        Step("B2 " + N + "MouseIndexBias",
            () => ExposureUnnoticed2.Scripts.Base.InputManager.MouseIndexBias);

        Step("B3 " + N + "isExist",
            () => ExposureUnnoticed2.Scripts.Base.InputManager.isExist);

        Step("B4 " + N + "IsGamePad",
            () => ExposureUnnoticed2.Scripts.Base.InputManager.IsGamePad);
    }

    /// <summary>Drop the cached game window handle so the next measurement re-resolves it.</summary>
    public static void InvalidateWindow()
    {
        _gameHwnd = IntPtr.Zero;
        Plugin.Logger.LogInfo("MM| window cache invalidated (display signature changed)");
    }

    // ---------------------------------- 16:10 fill (F12) - DEPRECATED in 0.14.0
    //  No longer bound to any key. Its premise was wrong: /Canvas/Title/Image has
    //  lossyScale=0.10 and is a 246x139 logo, NOT a full-screen 16:9 background layer.
    //  The black bars belong to the 16:9 backbuffer itself (see the AUTO guard above),
    //  so the resolution fix is the right repair. Kept for reference only.
    /// <summary>
    /// The game lays its full-screen background layers out as 1920x1080 (16:9) even when
    /// the canvas is 1920x1200 (16:10). On a 16:10 panel that leaves an 80px strip at the
    /// top and bottom showing the camera's clear colour. This finds those layers and grows
    /// them to the canvas height so the screen is actually covered.
    /// </summary>
    public static void Fill16By10(string why)
    {
        int hits = 0, changed = 0;
        try
        {
            int sw = Screen.width, sh = Screen.height;
            var rts = UnityEngine.Object.FindObjectsOfType<RectTransform>();
            foreach (var rt in rts)
            {
                if (rt == null) continue;
                try
                {
                    var r = rt.rect;
                    var ls = rt.lossyScale;
                    float pw = Math.Abs(r.width * ls.x);
                    float ph = Math.Abs(r.height * ls.y);
                    if (pw < sw * 0.97f) continue;   // must span the full width
                    if (ph >= sh * 0.985f) continue; // already covers the screen
                    if (ph < sh * 0.70f) continue;   // not a full-screen layer

                    hits++;
                    float wantCanvasH = r.height * (sh / ph);
                    Plugin.Logger.LogWarning(
                        $"MM| FILL '{PathOf(rt)}' phys={pw:F0}x{ph:F0} canvasH {r.height:F0} -> {wantCanvasH:F0}");
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, wantCanvasH);
                    changed++;
                }
                catch { }
            }
            Plugin.Logger.LogWarning($"MM| FILL done ({why}): {hits} candidate(s), {changed} stretched to fill {sw}x{sh}");
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| Fill16By10 failed: " + e); }
    }

    // ------------------------------------------------------- repair (F11)
    /// <summary>
    /// True when the window's client area does not match what Unity believes the
    /// screen is, or when the game window is larger than the desktop. That is the
    /// combination that leaves the game rendering only the top-left corner of its
    /// own window with the rest black.
    /// </summary>
    public static bool ViewportLooksWrong()
    {
        try
        {
            bool bad = false;

            var hwnd = GameWindow();
            RECT cr;
            if (Win32.GetClientRect(hwnd, out cr))
            {
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                if (cw != Screen.width || ch != Screen.height)
                {
                    bad = true;
                    Plugin.Logger.LogWarning(
                        $"MM| MISMATCH client={cw}x{ch} vs Unity Screen={Screen.width}x{Screen.height}");
                }
            }

            int deskW = Display.main.systemWidth;
            int deskH = Display.main.systemHeight;
            RECT wr;
            if (Win32.GetWindowRect(hwnd, out wr))
            {
                if (wr.Left < 0 || wr.Top < 0 || wr.Right > deskW || wr.Bottom > deskH)
                {
                    bad = true;
                    Plugin.Logger.LogWarning(
                        $"MM| MISMATCH game window ({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) " +
                        $"extends past the desktop {deskW}x{deskH}");
                }
            }

            return bad;
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| ViewportLooksWrong: " + e); return false; }
    }

    /// <summary>
    /// Ask Unity to re-create the swap chain and re-layout the viewport at the
    /// current size. This is what a successful Alt+Enter ends up doing, and it
    /// clears the "only the top-left corner renders" state.
    /// </summary>
    public static void ReapplyResolution(string why)
    {
        try
        {
            int w = Screen.width, h = Screen.height;
            var mode = Screen.fullScreenMode;
            Plugin.Logger.LogWarning($"MM| FIX re-applying resolution {w}x{h} {mode}   ({why})");
            Screen.SetResolution(w, h, mode);
            Plugin.Logger.LogWarning("MM| FIX requested - watch the BURST lines below to confirm");
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| ReapplyResolution failed: " + e); }
    }

    /// <summary>
    /// Force Unity to use the monitor's native resolution. The game itself only knows
    /// 16:9 / 21:9 entries, so on a 2560x1600 panel it settles on 2560x1440 and letterboxes
    /// the missing 160px. Setting the resolution directly makes Unity render the whole
    /// panel, which also rebuilds the viewport (repairing the black-screen state).
    /// </summary>
    public static void ForceNativeResolution(string why)
    {
        try
        {
            int w = Display.main.systemWidth;
            int h = Display.main.systemHeight;
            var mode = Screen.fullScreenMode;

            Plugin.Logger.LogWarning(
                $"MM| FIX forcing NATIVE resolution {w}x{h} {mode}  (currently {Screen.width}x{Screen.height})  ({why})");

            if (Screen.width == w && Screen.height == h)
                Plugin.Logger.LogWarning("MM| FIX already at native size - re-applying anyway to rebuild the viewport");

            Screen.SetResolution(w, h, mode);
            Plugin.Logger.LogWarning("MM| FIX requested - watch the BURST lines to confirm it took");
        }
        catch (Exception e) { Plugin.Logger.LogError("MM| ForceNativeResolution failed: " + e); }
    }

    // ---------------------------------------------- 16:10 support (F12)
    /// <summary>
    /// Append the monitor's native resolution to the game's own resolution list, so the
    /// game can offer and use the screen's true aspect ratio instead of only 16:9 / 21:9.
    /// WindowModeOptionApplyer.ResolutionDataList is a static List&lt;ResolutionData&gt; and
    /// ResolutionData has a public (int x, int y) constructor, so this is a plain list Add -
    /// no game method needs to be invoked and nothing is replaced.
    /// </summary>
    public static void InjectNativeResolution()
    {
        try
        {
            int w = Display.main.systemWidth;
            int h = Display.main.systemHeight;

            ExposureUnnoticed2.Scripts.Base.WindowModeOptionApplyer.InitializeIfNeed();

            var list = ExposureUnnoticed2.Scripts.Base.WindowModeOptionApplyer.ResolutionDataList;
            if (list == null)
            {
                Plugin.Logger.LogError("MM| INJECT: ResolutionDataList is null - aborting");
                return;
            }

            Plugin.Logger.LogWarning(
                $"MM| INJECT: target {w}x{h}; the game's list currently holds {list.Count} entries:");
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                Plugin.Logger.LogWarning(
                    $"MM| INJECT:   [{i}] " + (r == null ? "null" : $"{r.X}x{r.Y}  name='{r.Name}'"));

                if (r != null && r.X == w && r.Y == h)
                {
                    Plugin.Logger.LogWarning(
                        $"MM| INJECT: {w}x{h} is already present at [{i}] - nothing to do");
                    return;
                }
            }

            var item = new ExposureUnnoticed2.Scripts.Base.ResolutionData(w, h);
            try { item.Name = $"{w}x{h}"; } catch { }
            list.Add(item);

            Plugin.Logger.LogWarning(
                $"MM| INJECT: added {w}x{h} -> the list now holds {list.Count} entries. " +
                "Open the game's graphics settings; the new entry should be selectable there.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("MM| InjectNativeResolution failed: " + e);
        }
    }

    // ---------------------------------------------------------------- helpers
    private static string PathOf(Transform t)
    {
        try
        {
            var sb = new StringBuilder();
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 10)
            {
                sb.Insert(0, "/" + cur.gameObject.name);
                cur = cur.parent;
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    private static string SafeTypeName(object o)
    {
        try
        {
            var t = o.GetType();
            var fn = t != null ? t.FullName : null;
            if (!string.IsNullOrEmpty(fn)) return fn;
        }
        catch { }
        try { return o.ToString() ?? "(unknown)"; } catch { }
        return "(unknown)";
    }
}

// =========================================================================
//  Minimal Win32 P/Invoke
// =========================================================================
[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

internal static class Win32
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
