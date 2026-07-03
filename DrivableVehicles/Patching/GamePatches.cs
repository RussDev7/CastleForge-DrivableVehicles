/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7
This file is part of https://github.com/RussDev7/CastleForge - see LICENSE for details.

DrivableVehicles prototype changes:
- Uses a Harmony postfix on the HUD input path so vehicle driving receives the real InputManager.
- Keeps vanilla HUD input active so chat and the Escape menu still work while driving.
- Adds clean-HUD drawing patches that keep chat/distance while hiding the main HUD and construction selection.
- Suppresses held-item actions while driving so the player cannot mine, shoot, melee, or place blocks from the car.
*/

using Microsoft.Xna.Framework.Graphics;
using DNA.CastleMinerZ.Inventory;
using System.Collections.Generic;
using DNA.CastleMinerZ.Terrain;
using Microsoft.Xna.Framework;
using DNA.CastleMinerZ.UI;
using System.Reflection;
using DNA.CastleMinerZ;
using DNA.Drawing.UI;
using HarmonyLib;
using DNA.Input;
using System;
using DNA;

using static ModLoader.LogSystem;

namespace DrivableVehicles
{
    #region Harmony Bootstrap

    /// <summary>
    /// Harmony bootstrap and vehicle-specific gameplay patches.
    /// </summary>
    internal static class GamePatches
    {
        #region Fields

        /// <summary>
        /// Harmony instance used to apply and remove this mod's patches.
        /// </summary>
        private static Harmony _harmony;

        /// <summary>
        /// Unique Harmony ID used so only this mod's patches are removed on shutdown.
        /// </summary>
        private static string _harmonyId;

        #endregion

        #region Patch Lifecycle

        /// <summary>
        /// Applies all Harmony patches in this assembly.
        /// </summary>
        public static void ApplyAllPatches()
        {
            try
            {
                _harmonyId = $"castleminerz.mods.{typeof(GamePatches).Namespace}.patches";
                _harmony = new Harmony(_harmonyId);

                _harmony.PatchAll(typeof(GamePatches).Assembly);

                Log("Harmony patches applied.");
            }
            catch (Exception ex)
            {
                Log($"Harmony patching failed: {ex.GetType().Name}: {ex.Message}.");
            }
        }

        /// <summary>
        /// Removes this mod's Harmony patches.
        /// </summary>
        public static void DisableAll()
        {
            try
            {
                if (_harmony != null && !string.IsNullOrEmpty(_harmonyId))
                {
                    _harmony.UnpatchAll(_harmonyId);
                    Log("Harmony patches removed.");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to disable Harmony patches: {ex.Message}.");
            }
            finally
            {
                _harmony = null;
                _harmonyId = null;
            }
        }
        #endregion
    }
    #endregion

    #region HUD Input Hook

    /// <summary>
    /// Runs vehicle input after the normal HUD input has already handled chat,
    /// menus, inventory, and other vanilla controls.
    /// </summary>
    [HarmonyPatch]
    internal static class InGameHUDVehicleInputPatch
    {
        #region Harmony Target

        /// <summary>
        /// Targets InGameHUD.OnPlayerInput so vehicle controls can share the real input manager.
        /// </summary>
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(InGameHUD),
                "OnPlayerInput",
                new[] { typeof(InputManager), typeof(GameController), typeof(KeyboardInput), typeof(GameTime) });
        }
        #endregion

        #region Patch Methods

        /// <summary>
        /// Clears held-item use before vanilla input runs when vehicle action blocking is active.
        /// </summary>
        private static void Prefix(InGameHUD __instance)
        {
            if (VehicleRuntime.BlockPlayerItemActions)
                VehicleRuntime.SuppressHeldItemUse(__instance);
        }

        /// <summary>
        /// Updates vehicle driving from the HUD input path after chat and menus have processed input.
        /// </summary>
        private static void Postfix(InputManager inputManager, GameTime gameTime)
        {
            VehicleRuntime.TickFromHudInput(inputManager, gameTime);
        }
        #endregion
    }
    #endregion

    #region Clean HUD Patches

    /// <summary>
    /// When clean HUD is enabled, replace the normal HUD draw with only the console/chat
    /// and distance readout. This avoids hiding chat the way the full /toggleui patch does.
    /// </summary>
    [HarmonyPatch]
    internal static class InGameHUDCleanHudDrawPatch
    {
        #region Reflection Fields

        /// <summary>
        /// Backing field for the HUD title-safe rectangle cache.
        /// </summary>
        private static readonly FieldInfo _prevTitleSafeField =
            AccessTools.Field(typeof(InGameHUD), "_prevTitleSafe");

        /// <summary>
        /// Backing field for the plain chat screen so clean HUD can keep chat input aligned.
        /// </summary>
        private static readonly FieldInfo _chatScreenField =
            AccessTools.Field(typeof(InGameHUD), "_chatScreen");

        #endregion

        #region Harmony Target

        /// <summary>
        /// Targets InGameHUD.OnDraw so clean HUD can draw only chat/console and distance text.
        /// </summary>
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(InGameHUD),
                "OnDraw",
                new[] { typeof(GraphicsDevice), typeof(SpriteBatch), typeof(GameTime) });
        }
        #endregion

        #region Patch Methods

        /// <summary>
        /// Draws the minimal vehicle HUD and skips vanilla HUD drawing while clean HUD is enabled.
        /// </summary>
        /// <remarks>
        /// Note: the fallback path intentionally returns true on errors so vanilla HUD drawing resumes.
        /// </remarks>
        private static bool Prefix(InGameHUD __instance, GraphicsDevice device, SpriteBatch spriteBatch, GameTime gameTime)
        {
            if (!VehicleRuntime.CleanHudEnabled)
                return true;

            bool spriteBatchBegun = false;

            try
            {
                VehicleRuntime.ApplyCleanHudAvatarVisibility();

                Rectangle titleSafe = new Rectangle(
                    0,
                    0,
                    Screen.Adjuster.ScreenRect.Width,
                    Screen.Adjuster.ScreenRect.Height);

                Rectangle previousTitleSafe = Rectangle.Empty;
                if (_prevTitleSafeField != null)
                    previousTitleSafe = (Rectangle)_prevTitleSafeField.GetValue(__instance);

                if (titleSafe != previousTitleSafe && __instance.console != null)
                {
                    __instance.console.Location = new Vector2((float)titleSafe.Left, (float)titleSafe.Top);

                    PlainChatInputScreen chatScreen =
                        _chatScreenField != null ? _chatScreenField.GetValue(__instance) as PlainChatInputScreen : null;

                    if (chatScreen != null)
                        chatScreen.YLoc = __instance.console.Bounds.Bottom + 25f;
                }

                __instance.console?.Draw(device, spriteBatch, gameTime, false);

                spriteBatch.Begin();
                spriteBatchBegun = true;

                __instance.DrawDistanceStr(spriteBatch);

                spriteBatch.End();
                spriteBatchBegun = false;

                _prevTitleSafeField?.SetValue(__instance, titleSafe);

                return false;
            }
            catch (Exception ex)
            {
                if (spriteBatchBegun)
                {
                    try { spriteBatch.End(); }
                    catch { }
                }

                Log($"Clean HUD draw failed; falling back to vanilla HUD: {ex.Message}.");
                return true;
            }
        }
        #endregion
    }

    /// <summary>
    /// Hides/suppresses the construction selection probe while clean HUD is enabled.
    /// This removes the selected-block/build outline without disabling chat or Escape.
    /// </summary>
    [HarmonyPatch(typeof(InGameHUD))]
    [HarmonyPatch("DoConstructionModeUpdate")]
    internal static class InGameHUDCleanHudConstructionPatch
    {
        #region Patch Methods

        /// <summary>
        /// Skips construction-mode probing while clean HUD is active.
        /// </summary>
        private static bool Prefix()
        {
            return !VehicleRuntime.CleanHudEnabled;
        }
        #endregion
    }
    #endregion

    #region Held-Item Action Blocking

    /// <summary>
    /// Blocks active held-item processing while driving.
    /// This is the main guard against left-click mining/shooting/melee/block placement,
    /// while the normal HUD input path still handles chat and Escape.
    /// </summary>
    [HarmonyPatch]
    internal static class VehicleHeldItemInputBlockPatch
    {
        #region Harmony Targets

        /// <summary>
        /// Targets ProcessInput on item types that can mine, shoot, melee, throw, place, or use explosives.
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] inventoryTypes = new[]
            {
                typeof(InventoryItem),
                typeof(BlockInventoryItem),
                typeof(GunInventoryItem),
                typeof(PickInventoryItem),
                typeof(GrenadeItem),
                typeof(GrenadeLauncherBaseItem),
                typeof(RocketLauncherBaseItem),
                typeof(StickyGrenadeItem)
            };

            Type[] signature = new[] { typeof(InGameHUD), typeof(CastleMinerZControllerMapping) };

            for (int i = 0; i < inventoryTypes.Length; i++)
            {
                MethodInfo method = AccessTools.Method(inventoryTypes[i], "ProcessInput", signature);
                if (method != null)
                    yield return method;
            }
        }
        #endregion

        #region Patch Methods

        /// <summary>
        /// Blocks item input while driving and clears any in-progress held-item use.
        /// </summary>
        private static bool Prefix(InGameHUD hud)
        {
            if (!VehicleRuntime.BlockPlayerItemActions)
                return true;

            VehicleRuntime.SuppressHeldItemUse(hud);
            return false;
        }
        #endregion
    }

    /// <summary>
    /// Backstop for direct action methods in case an item bypasses ProcessInput or another mod calls them.
    /// </summary>
    [HarmonyPatch]
    internal static class VehicleDirectActionBlockPatch
    {
        #region Harmony Targets

        /// <summary>
        /// Targets direct HUD action methods that can still perform gameplay actions while driving.
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo method;

            method = AccessTools.Method(typeof(InGameHUD), "Shoot", new[] { typeof(GunInventoryItemClass) });
            if (method != null) yield return method;

            method = AccessTools.Method(typeof(InGameHUD), "Dig", new[] { typeof(InventoryItem), typeof(bool) });
            if (method != null) yield return method;

            method = AccessTools.Method(typeof(InGameHUD), "Melee", new[] { typeof(InventoryItem) });
            if (method != null) yield return method;

            method = AccessTools.Method(typeof(InGameHUD), "MeleePlayer", new[] { typeof(InventoryItem), typeof(Player) });
            if (method != null) yield return method;

            method = AccessTools.Method(typeof(InGameHUD), "SetFuseForExplosive", new[] { typeof(IntVector3), typeof(ExplosiveTypes) });
            if (method != null) yield return method;

            method = AccessTools.Method(typeof(InGameHUD), "UseDoor", new[] { typeof(BlockTypeEnum), typeof(BlockTypeEnum) });
            if (method != null) yield return method;
        }
        #endregion

        #region Patch Methods

        /// <summary>
        /// Blocks direct HUD actions while driving and clears any in-progress held-item use.
        /// </summary>
        private static bool Prefix(InGameHUD __instance)
        {
            if (!VehicleRuntime.BlockPlayerItemActions)
                return true;

            VehicleRuntime.SuppressHeldItemUse(__instance);
            return false;
        }
        #endregion
    }

    /// <summary>
    /// Backstop for block-placement validation while driving.
    /// </summary>
    [HarmonyPatch(typeof(InGameHUD), "Build")]
    internal static class VehicleBuildBlockPatch
    {
        #region Patch Methods

        /// <summary>
        /// Prevents block placement/build targeting while driving.
        /// </summary>
        private static bool Prefix(InGameHUD __instance, ref IntVector3 __result)
        {
            if (!VehicleRuntime.BlockPlayerItemActions)
                return true;

            VehicleRuntime.SuppressHeldItemUse(__instance);
            __result = IntVector3.Zero;
            return false;
        }
        #endregion
    }
    #endregion
}