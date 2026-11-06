using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AquaMai.Config.Attributes;
using AquaMai.Core;
using AquaMai.Core.Attributes;
using AquaMai.Core.Helpers;
using HarmonyLib;
using UnityEngine;

namespace AquaMai.Mods.GameSystem;

[ConfigSection(
    name: "图形设置",
    en: "Resolution Settings / Windowed Mode.",
    zh: "分辨率设置 / 窗口化")]
[EnableGameVersion(23000)]
public class Window
{
    [ConfigEntry(
        name: "窗口模式",
        en: "Window the game.",
        zh: "窗口化游戏")]
    private static readonly bool windowed = false;

    [ConfigEntry(
        name: "无边框",
        en: "Borderless window.")]
    private static readonly bool borderless = false;

    [ConfigEntry(
        name: "宽度",
        en: """
            Window width (and height) for windowed mode, rendering resolution for fullscreen mode.
            If set to 0, windowed mode will remember the user-set size, fullscreen mode will use the current display resolution.
            If the display size is incorrect, please set it to the screen resolution.
            DisplayScale uses this size.
            """,
        zh: """
            宽度（和高度）窗口化时为游戏窗口大小，全屏时为渲染分辨率
            如果设为 0，窗口化将记住用户设定的大小，全屏时将使用当前显示器分辨率
            全屏时如果显示大小不正确，请改成屏幕的分辨率（如 2160x3840）
            画面缩放（DisplayScale）使用该尺寸
            """)]
    private static readonly int width = 0;

    [ConfigEntry(
        name: "高度",
        en: "Height, as above.")]
    private static readonly int height = 0;

    [ConfigEntry(
        name: "独占全屏",
        en: """
            Exclusive fullscreen instead of a fullscreen window (fullscreen mode only).
            Skips desktop composition, which lowers and steadies display latency. The game minimizes when it loses focus.
            Needs the game to render on the GPU that drives the display, and Windows "fullscreen optimizations" disabled for Sinmai.exe
            (compatibility settings); otherwise Windows emulates it as a composed window, which is slower than the default.
            """,
        zh: """
            使用独占全屏而不是全屏窗口（仅全屏模式）
            跳过桌面合成，显示延迟更低更稳定。游戏失去焦点时会最小化
            需要游戏在驱动该显示器的 GPU 上渲染，并为 Sinmai.exe 禁用 Windows 的“全屏优化”（兼容性设置），
            否则 Windows 会用合成窗口模拟独占全屏，延迟反而比默认更高
            """)]
    private static readonly bool exclusiveFullscreen = false;

    [ConfigEntry(
        name: "画面缩放",
        en: """
            Scale the game display inside the existing window without changing the window size.
            1.0 is the original pixel size (1080 wide per player). Values below 1 shrink it.
            Applies to 1P, 2P, or both, depending on which sides are shown.
            Width/height 0 uses the current window or display (e.g. fullscreen).
            """,
        zh: """
            在不改变窗口大小的情况下缩放游戏画面
            1.0 为原始像素大小（每名玩家宽 1080），小于 1 会缩小
            根据当前显示的 1P / 2P 生效
            宽高为 0 时使用当前窗口或显示器大小（例如全屏）
            """)]
    public static readonly float displayScale = 1.0f;

    private const float MinDisplayScale = 0.1f;
    private const float MaxDisplayScale = 4.0f;
    private const float PlayerAreaWidth = 1080f;

    private const int GWL_STYLE = -16;
    private const uint WS_WHATEVER = 0x14CF0000;
    private const uint WS_VISIBLE = 0x10000000;    
    private const uint WS_POPUP = 0x80000000;

    private static IntPtr hwnd = IntPtr.Zero;

    /// <summary>
    /// Pixel width of the game output. An explicit Window width if set; otherwise the
    /// current window, or the display size when width is 0 in fullscreen.
    /// </summary>
    public static int GetOutputWidth()
    {
        if (width != 0) return width;
        if (windowed && Screen.width > 0) return Screen.width;
        if (IsSectionEnabled()) return Display.main.systemWidth;
        return Screen.width > 0 ? Screen.width : Display.main.systemWidth;
    }

    /// <summary>
    /// Pixel height of the game output. See <see cref="GetOutputWidth"/>.
    /// </summary>
    public static int GetOutputHeight()
    {
        if (height != 0) return height;
        if (windowed && Screen.height > 0) return Screen.height;
        if (IsSectionEnabled()) return Display.main.systemHeight;
        return Screen.height > 0 ? Screen.height : Display.main.systemHeight;
    }

    private static bool IsSectionEnabled()
    {
        return ConfigLoader.Config != null && ConfigLoader.Config.GetSectionState(typeof(Window)).Enabled;
    }

    private static bool IsSinglePlayerEnabled()
    {
        return ConfigLoader.Config != null && ConfigLoader.Config.GetSectionState(typeof(SinglePlayer)).Enabled;
    }

    public static float GetDisplayScale()
    {
        if (float.IsNaN(displayScale) || float.IsInfinity(displayScale))
        {
            return 1.0f;
        }

        return Mathf.Clamp(displayScale, MinDisplayScale, MaxDisplayScale);
    }

    public static void ApplyDisplayViewport(Camera camera)
    {
        if (camera == null || !IsSectionEnabled()) return;

        var scale = GetDisplayScale();
        var outputW = GetOutputWidth();
        var outputH = GetOutputHeight();
        if (outputW <= 0 || outputH <= 0) return;

        var contentW = IsSinglePlayerEnabled() ? PlayerAreaWidth : PlayerAreaWidth * 2f;
        var contentH = camera.orthographicSize * 2f;
        var viewW = contentW * scale / outputW;
        var viewH = contentH * scale / outputH;
        if (viewW > 1f || viewH > 1f)
        {
            var fit = Mathf.Min(1f / viewW, 1f / viewH);
            viewW *= fit;
            viewH *= fit;
            scale *= fit;
        }

        camera.rect = new Rect((1f - viewW) / 2f, (1f - viewH) / 2f, viewW, viewH);
        GuiSizes.PlayerWidthOverride = PlayerAreaWidth * scale;
    }

    public static void EnsureDisplayViewportUpdater(Camera camera)
    {
        if (camera == null || !IsSectionEnabled()) return;
        if (camera.GetComponent<DisplayViewportUpdater>() == null)
        {
            camera.gameObject.AddComponent<DisplayViewportUpdater>();
        }
    }

    private class DisplayViewportUpdater : MonoBehaviour
    {
        private int lastWidth;
        private int lastHeight;

        private void Start()
        {
            lastWidth = Screen.width;
            lastHeight = Screen.height;
        }

        private void Update()
        {
            if (Screen.width == lastWidth && Screen.height == lastHeight) return;
            lastWidth = Screen.width;
            lastHeight = Screen.height;
            ApplyDisplayViewport(Camera.main);
        }
    }

    [HarmonyPatch]
    public class ApplyDisplayScale
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return SinglePlayer.WhateverInitialize.TargetMethods();
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            ApplyDisplayViewport(Camera.main);
            EnsureDisplayViewportUpdater(Camera.main);
        }
    }

    public static void OnBeforePatch()
    {
        if (windowed)
        {
            var alreadyWindowed = Screen.fullScreenMode == FullScreenMode.Windowed;
            if (width == 0 || height == 0)
            {
                Screen.fullScreenMode = FullScreenMode.Windowed;
            }
            else
            {
                alreadyWindowed = false;
                Screen.SetResolution(width, height, FullScreenMode.Windowed);
            }

            hwnd = GetWindowHandle();
            if (alreadyWindowed)
            {
                SetResizeable();
            }
            else
            {
                Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    // Screen.SetResolution has delay
                    SetResizeable();
                });
            }
        }
        else
        {
            Screen.SetResolution(GetOutputWidth(), GetOutputHeight(), exclusiveFullscreen ? FullScreenMode.ExclusiveFullScreen : FullScreenMode.FullScreenWindow);
        }
    }

    public static void SetResizeable()
    {
        if (hwnd == IntPtr.Zero) return;
        if (borderless)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
        }
        else
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, WS_WHATEVER);
        }
    }

    private delegate bool EnumThreadDelegate(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumThreadWindows(int dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);

    [DllImport("Kernel32.dll")]
    static extern int GetCurrentThreadId();

    static IntPtr GetWindowHandle()
    {
        IntPtr returnHwnd = IntPtr.Zero;
        var threadId = GetCurrentThreadId();
        EnumThreadWindows(threadId,
            (hWnd, lParam) =>
            {
                if (returnHwnd == IntPtr.Zero) returnHwnd = hWnd;
                return true;
            }, IntPtr.Zero);
        return returnHwnd;
    }

    [DllImport("user32.dll")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, uint dwNewLong);
}
