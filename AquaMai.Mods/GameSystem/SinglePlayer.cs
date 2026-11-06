using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AquaMai.Core;
using AquaMai.Core.Attributes;
using AquaMai.Config.Attributes;
using AquaMai.Core.Helpers;
using AquaMai.Mods.Fancy.GamePlay;
using AquaMai.Mods.Utils;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Common;
using Monitor.Entry;
using Monitor.Entry.Parts.Screens;
using UnityEngine;
using Fx;
using Process;

namespace AquaMai.Mods.GameSystem;

// Hides the 2p (right hand side) UI.
// Note: this is not my original work. I simply interpreted the code and rewrote it as a mod.
[ConfigSection(
    name: "单人模式",
    en: """
        Single player: Show 1P only, at the center of the screen.
        """,
    zh: """
        单人模式，不显示 2P
        """)]
public partial class SinglePlayer
{
    [ConfigEntry(
        name: "隐藏副屏",
        en: "Only show the main area, without the sub-monitor.",
        zh: "只显示主区域，不显示副屏")]
    public static bool HideSubMonitor = false;

    [ConfigEntry(
        name: "1P画面缩放",
        en: "Scale the 1P display inside the existing game window. 1.0 is the original size; values below 1 shrink it.",
        zh: "在不改变游戏窗口大小的情况下调整 1P 画面比例。1.0 为原始大小，小于 1 会缩小。")]
    public static float displayScale = 1.0f;

    private const float MinDisplayScale = 0.1f;
    private const float MaxDisplayScale = 4.0f;
    private const float MainAreaHeight = 1080f;
    private const float FullAreaHeight = 1920f;
    private const float MainAreaCenterOffsetY = (MainAreaHeight - FullAreaHeight) / 2f;

    private const string ScreenPositionAdjustMain1P = "[AquaMai] ScreenPositionAdjust Image Main 1P";
    private const string ScreenPositionAdjustSub1P = "[AquaMai] ScreenPositionAdjust Image Sub 1P";
    private const string ScreenPositionAdjustMain2P = "[AquaMai] ScreenPositionAdjust Image Main 2P";
    private const string ScreenPositionAdjustSub2P = "[AquaMai] ScreenPositionAdjust Image Sub 2P";

    private static void HideScreenPositionAdjustImage(string name)
    {
        var image = GameObject.Find(name);
        if (image != null)
        {
            image.transform.localScale = Vector3.zero;
        }
    }

    private static float GetDisplayScale()
    {
        if (float.IsNaN(displayScale) || float.IsInfinity(displayScale))
        {
            return 1.0f;
        }

        return Mathf.Clamp(displayScale, MinDisplayScale, MaxDisplayScale);
    }

    private static bool IsScreenPositionAdjustEnabled()
    {
        return ConfigLoader.Config.GetSectionState(typeof(ScreenPositionAdjust)).Enabled;
    }

    [HarmonyPatch]
    public class WhateverInitialize
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var lateInitialize = AccessTools.Method(typeof(Main.GameMain), "LateInitialize", [typeof(MonoBehaviour), typeof(Transform), typeof(Transform)]);
            if (lateInitialize is not null) return [lateInitialize];
            return [AccessTools.Method(typeof(Main.GameMain), "Initialize", [typeof(MonoBehaviour), typeof(Transform), typeof(Transform)])];
        }

        public static void Prefix(MonoBehaviour gameMainObject, ref Transform left, ref Transform right)
        {
            // ScreenPositionAdjust installs its own render-texture camera and output canvas.
            // Its matching postfix below applies the single-player settings after that setup.
            if (IsScreenPositionAdjustEnabled()) return;

            var mainCamera = Camera.main;
            if (mainCamera != null)
            {
                var position = mainCamera.transform.position;
                var orthographicSize = mainCamera.orthographicSize;

                if (HideSubMonitor)
                {
                    position.y += MainAreaCenterOffsetY;
                    orthographicSize = MainAreaHeight / 2f;
                }

                mainCamera.transform.position = new Vector3(position.x - 540f, position.y, position.z);
                mainCamera.orthographicSize = orthographicSize / GetDisplayScale();
            }

            if (right != null)
            {
                right.localScale = Vector3.zero;
            }
        }
    }

    /// <summary>
    /// ScreenPositionAdjust creates its display images in a prefix on the same game
    /// initialization method. Apply our changes afterward so its camera setup cannot
    /// overwrite the single-player crop or scale.
    /// </summary>
    [HarmonyPatch]
    public class WhateverInitializePostfix
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return WhateverInitialize.TargetMethods();
        }

        [HarmonyPostfix]
        public static void Postfix(ref Transform right)
        {
            if (!IsScreenPositionAdjustEnabled()) return;

            if (right != null)
            {
                right.localScale = Vector3.zero;
            }

            // The compositor creates both players' images. Hide the 2P images too,
            // otherwise a wide window could expose their blank render-texture area.
            HideScreenPositionAdjustImage(ScreenPositionAdjustMain2P);
            HideScreenPositionAdjustImage(ScreenPositionAdjustSub2P);

            var mainCamera = Camera.main;
            if (mainCamera == null) return;

            if (HideSubMonitor)
            {
                // In ScreenPositionAdjust, the main image can be vertically offset
                // (including when its compact layout is enabled). Use its actual
                // center rather than assuming a fixed offset.
                var mainImage = GameObject.Find(ScreenPositionAdjustMain1P);
                if (mainImage != null)
                {
                    var position = mainImage.transform.position;
                    var cameraPosition = mainCamera.transform.position;
                    mainCamera.transform.position = new Vector3(position.x, position.y, cameraPosition.z);
                }
                else
                {
                    mainCamera.transform.position += new Vector3(0f, MainAreaCenterOffsetY, 0f);
                }

                HideScreenPositionAdjustImage(ScreenPositionAdjustSub1P);
                mainCamera.orthographicSize = MainAreaHeight / 2f;
            }

            // Scaling the display camera keeps the Unity window dimensions unchanged
            // while shrinking or enlarging the complete 1P output within that window.
            mainCamera.orthographicSize /= GetDisplayScale();
        }
    }

    [ConfigEntry(
        name: "自动跳过倒计时",
        en: "Automatically skip the countdown when logging in with a card in single-player mode.",
        zh: "单人模式下刷卡登录直接进入下一个界面，无需跳过倒计时")]
    public static bool autoSkip = false;

    [EnableGameVersion(23000, noWarn: true)]
    [EnableIf(nameof(autoSkip))]
    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntryMonitor), "DecideEntry")]
    public static void PostDecideEntry(EntryMonitor __instance)
    {
# if DEBUG
        MelonLogger.Msg("Confirm Entry");
# endif
        TimeManager.MarkGameStartTime();
        Singleton<EventManager>.Instance.UpdateEvent();
        Singleton<ScoreRankingManager>.Instance.UpdateData();
        SharedInstances.GameMainObject.StartCoroutine(LaterDisableCardReader());
        __instance.Process.CreateDownloadProcess();
        __instance.ProcessManager.SendMessage(new Message(ProcessType.CommonProcess, 30001));
        __instance.ProcessManager.SendMessage(new Message(ProcessType.CommonProcess, 40000, 0, OperationInformationController.InformationType.Hide));
        __instance.Process.SetNextProcess();
    }

    private static IEnumerator LaterDisableCardReader()
    {
        yield return new WaitForSeconds(3);
        SingletonStateMachine<AmManager, AmManager.EState>.Instance.AimeReader.EnableRead(flag: false);
    }

    // To prevent the "長押受付終了" overlay from appearing
    [EnableGameVersion(21500, noWarn: true)]
    [EnableIf(nameof(autoSkip))]
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WaitPartner), "Open")]
    public static bool WaitPartnerPreOpen()
    {
        return false;
    }

    [ConfigEntry(
        name: "修复烟花效果",
        en: "Fix hanabi effect under single-player mode (disabled automatically if HideHanabi is enabled).",
        zh: "修复单人模式下的烟花效果（如果启用了 HideHanabi，则会自动禁用）")]
    public static bool fixHanabi = true;

    private static bool fixHanabiDisableImplied = false;
    private static bool FixHanabiEnabled => fixHanabi && !fixHanabiDisableImplied;

    [EnableIf(nameof(FixHanabiEnabled))]
    [HarmonyPatch(typeof(TapCEffect), "SetUpParticle")]
    [HarmonyPostfix]
    public static void PostSetUpParticle(TapCEffect __instance, FX_Mai2_Note_Color ____particleControler)
    {
        var entities = ____particleControler.GetComponentsInChildren<ParticleSystemRenderer>(true);
        foreach (var entity in entities)
        {
            entity.maxParticleSize = 1f;
        }
    }

    public static void OnBeforePatch()
    {
        if (ConfigLoader.Config.GetSectionState(typeof(HideHanabi)).Enabled)
        {
            fixHanabiDisableImplied = true;
        }
    }

    public static void OnAfterPatch()
    {
        Core.Helpers.GuiSizes.SinglePlayer = true;
    }

    [ConfigEntry(
        name: "显示自由模式计时",
        en: "Show the timer in free mode on the sub-monitor.",
        zh: "使用自由模式时，在副屏显示计时器")]
    public static bool showFreedomModeTimer = true;

    [EnableIf(nameof(showFreedomModeTimer))]
    [HarmonyPatch(typeof(PleaseWaitProcess), "OnStart")]
    [HarmonyPostfix]
    public static void FreedomModeTimerHook(PleaseWaitMonitor[] ____monitors)
    {
        var target = ____monitors[0].transform.Find("Canvas/Sub");
        var timer10 = ____monitors[1].transform.Find("Canvas/Main/FreedomMode/UI_Timer10");
        var timer1 = ____monitors[1].transform.Find("Canvas/Main/FreedomMode/UI_Timer1");

        target.gameObject.SetActive(true);
        var go = new GameObject("[AquaMai] 自由模式计时器");
        go.transform.localScale = Vector3.one * 0.4f;
        go.transform.position = new Vector3(390, 50, 0);
        go.transform.SetParent(target, false);
        timer1.transform.SetParent(go.transform, false);
        timer10.transform.SetParent(go.transform, false);
    }

    [EnableIf(nameof(showFreedomModeTimer))]
    [HarmonyPatch(typeof(PleaseWaitMonitor), "Initialize")]
    [HarmonyPostfix]
    public static void PleaseWaitMonitorInitialize(PleaseWaitMonitor.SpriteTimer ____time1, PleaseWaitMonitor.SpriteTimer ____time10)
    {
        ____time1.SetVisible(false);
        ____time10.SetVisible(false);
    }

    [EnableGameVersion(27000, noWarn: true)]
    [HarmonyPatch("ScreenClipSetter", "SetClipX")]
    [HarmonyPrefix]
    public static void ScreenClipSetterSetClipX(ref float minX, ref float maxX)
    {
        if (maxX <= 0.5f)
        {
            minX = 0f;
            maxX = 1f;
        }
    }
}