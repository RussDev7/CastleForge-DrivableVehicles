/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7
This file is part of https://github.com/RussDev7/CastleForge - see LICENSE for details.
*/

using System.Collections.Generic;
using DNA.CastleMinerZ.Terrain;
using Microsoft.Xna.Framework;
using DNA.CastleMinerZ.UI;
using DNA.CastleMinerZ;
using DNA.Input;
using System;

using static ModLoader.LogSystem;

namespace DrivableVehicles
{
    /// <summary>
    /// Shared local runtime state for prototype vehicles.
    /// 
    /// This is intentionally local-only. It does not create network messages,
    /// save data, crafting items, or persistent world entities yet.
    /// </summary>
    internal static class VehicleRuntime
    {
        #region Constants And Runtime State

        // Note: this runtime owns only local prototype state. It does not persist vehicles or sync them over multiplayer.

        private const float EnterDistance = 6f;

        private static float _modelScale = 10f;
        private static float _modelYawOffsetRadians = MathHelper.PiOver2;
        private static float _modelYOffset = 0.5f;
        private static bool  _cloneMissingWheels = true;
        private static bool  _manualCleanHudEnabled = false;
        private static bool  _vehicleCleanHudOverride = false;
        private static bool  _avatarVisibilityCaptured = false;
        private static bool  _capturedAvatarVisible = true;

        private static readonly List<PrototypeVehicleEntity> _vehicles = new List<PrototypeVehicleEntity>();
        private static readonly Vector3 _exitOffset = new Vector3(2.25f, 0.2f, 0f);

        private static PrototypeVehicleEntity _activeVehicle;
        private static bool _changedCameraMode;

        #endregion

        #region Public Runtime State

        /// <summary>
        /// True while the local player is driving a prototype vehicle.
        /// </summary>
        public static bool IsDriving => _activeVehicle != null;

        /// <summary>
        /// Visual scale applied to loaded XNB vehicle models.
        /// </summary>
        public static float ModelScale => _modelScale;

        /// <summary>
        /// Extra yaw applied to loaded XNB vehicle models when their forward direction differs from the prototype.
        /// </summary>
        public static float ModelYawOffsetRadians => _modelYawOffsetRadians;

        /// <summary>
        /// Vertical visual offset applied to loaded XNB vehicle models.
        /// Blender's Z axis maps to this game's world Y axis.
        /// </summary>
        public static float ModelYOffset => _modelYOffset;

        /// <summary>
        /// True when the renderer should draw the imported Wheel_1 mesh at the other wheel bones.
        /// This works around XNB imports where Unity shared wheel meshes only become one ModelMesh.
        /// </summary>
        public static bool CloneMissingWheels => _cloneMissingWheels;

        /// <summary>
        /// True while the minimal vehicle HUD is enabled.
        /// </summary>
        public static bool CleanHudEnabled => _manualCleanHudEnabled || _vehicleCleanHudOverride;

        /// <summary>
        /// True when the user manually enabled clean HUD outside of the automatic driving override.
        /// </summary>
        public static bool ManualCleanHudEnabled => _manualCleanHudEnabled;

        /// <summary>
        /// True while driving suppresses held-item actions such as mining, shooting, placing blocks, and melee.
        /// </summary>
        public static bool BlockPlayerItemActions => IsDriving;

        #endregion

        #region Feedback Helpers

        /// <summary>
        /// Returns true when the caller requested routine vehicle-state feedback and the master config allows it.
        /// </summary>
        private static bool ShouldShowVehicleStateFeedback(bool requested)
        {
            return requested && VehicleConfig.VehicleStateFeedbackEnabled;
        }
        #endregion

        #region Visual Tuning

        // Note: these settings affect only the rendered vehicle model. They do not change driving physics or collision.

        /// <summary>
        /// Sets the visual model scale for newly and already spawned vehicles.
        /// </summary>
        public static void SetModelScale(float scale)
        {
            _modelScale = MathHelper.Clamp(scale, 0.001f, 1000f);
            SendFeedback($"Model scale set to {_modelScale:0.###}.");
        }

        /// <summary>
        /// Sets the visual model yaw offset in degrees.
        /// </summary>
        public static void SetModelYawOffsetDegrees(float degrees)
        {
            _modelYawOffsetRadians = MathHelper.ToRadians(degrees);
            SendFeedback($"Model yaw offset set to {degrees:0.#} degrees.");
        }

        /// <summary>
        /// Sets the vertical visual model offset.
        /// Use this when the converted model sits too low/high without re-exporting from Blender.
        /// </summary>
        public static void SetModelYOffset(float offset)
        {
            _modelYOffset = MathHelper.Clamp(offset, -1000f, 1000f);
            SendFeedback($"Model Z/up offset set to {_modelYOffset:0.###} game units. Blender Z maps to game Y.");
        }

        /// <summary>
        /// Enables or disables the temporary wheel-cloning draw workaround.
        /// </summary>
        public static void SetCloneMissingWheels(bool enabled)
        {
            _cloneMissingWheels = enabled;
            SendFeedback("Missing-wheel clone workaround " + (_cloneMissingWheels ? "enabled." : "disabled."));
        }
        #endregion

        #region Clean HUD And Avatar Visibility

        // Note: the automatic vehicle override preserves the user's manual clean-HUD preference.

        /// <summary>
        /// Enables or disables the minimal driving HUD.
        /// This hides the player/avatar model and most HUD widgets while leaving chat and distance visible.
        /// </summary>
        public static void SetCleanHudEnabled(bool enabled)
        {
            bool wasEnabled = CleanHudEnabled;

            if (_manualCleanHudEnabled == enabled)
            {
                SendFeedback("Clean HUD manual preference is already " + (_manualCleanHudEnabled ? "enabled." : "disabled."));
                return;
            }

            _manualCleanHudEnabled = enabled;
            RefreshCleanHudAvatarVisibility(wasEnabled);

            SendFeedback("Clean HUD manual preference " + (_manualCleanHudEnabled ? "enabled." : "disabled.") +
                         (CleanHudEnabled ? " Effective clean HUD is enabled." : " Effective clean HUD is disabled."));
        }

        /// <summary>
        /// Enables the vehicle-owned clean HUD override while driving without changing the user's manual preference.
        /// </summary>
        private static void BeginVehicleCleanHudOverride()
        {
            bool wasEnabled = CleanHudEnabled;
            _vehicleCleanHudOverride = true;
            RefreshCleanHudAvatarVisibility(wasEnabled);
        }

        /// <summary>
        /// Removes the vehicle-owned clean HUD override and restores the user's manual clean HUD preference.
        /// </summary>
        private static void EndVehicleCleanHudOverride()
        {
            bool wasEnabled = CleanHudEnabled;
            _vehicleCleanHudOverride = false;
            RefreshCleanHudAvatarVisibility(wasEnabled);
        }

        /// <summary>
        /// Applies or restores avatar visibility when the effective clean-HUD state changes.
        /// </summary>
        private static void RefreshCleanHudAvatarVisibility(bool wasEnabled)
        {
            if (CleanHudEnabled)
                ApplyCleanHudAvatarVisibility();
            else if (wasEnabled)
                RestoreCleanHudAvatarVisibility();
        }

        /// <summary>
        /// Forces the local avatar hidden while the minimal driving HUD is enabled.
        /// </summary>
        public static void ApplyCleanHudAvatarVisibility()
        {
            if (!CleanHudEnabled)
                return;

            var player = CastleMinerZGame.Instance?.LocalPlayer;
            if (player == null || player.Avatar == null)
                return;

            if (!_avatarVisibilityCaptured)
            {
                _capturedAvatarVisible = player.Avatar.Visible;
                _avatarVisibilityCaptured = true;
            }

            player.Avatar.Visible = false;
        }

        /// <summary>
        /// Restores avatar visibility captured when the minimal driving HUD was enabled.
        /// </summary>
        public static void RestoreCleanHudAvatarVisibility()
        {
            var player = CastleMinerZGame.Instance?.LocalPlayer;

            if (_avatarVisibilityCaptured && player != null && player.Avatar != null)
                player.Avatar.Visible = _capturedAvatarVisible;

            _avatarVisibilityCaptured = false;
        }

        /// <summary>
        /// Disables the clean HUD without writing chat feedback. Used during shutdown.
        /// </summary>
        public static void DisableCleanHudSilently()
        {
            _manualCleanHudEnabled = false;
            _vehicleCleanHudOverride = false;
            RestoreCleanHudAvatarVisibility();
        }
        #endregion

        #region Model Selection And Diagnostics

        // Note: model selection refreshes already spawned prototype vehicles so XNB tuning can be tested live.

        /// <summary>
        /// Selects the active XNB model folder and refreshes already spawned prototype vehicles.
        /// </summary>
        public static void SelectModel(string modelName)
        {
            if (!VehicleContent.SetActiveModelName(modelName, out string message))
            {
                SendFeedback("" + message);
                return;
            }

            int refreshedCount = RefreshSpawnedVehicleModels();
            VehicleAudio.ReloadForActiveModel();

            if (refreshedCount > 0)
            {
                string vehicleText = refreshedCount == 1 ? "vehicle" : "vehicles";
                SendFeedback($"{message} Refreshed {refreshedCount} spawned {vehicleText}.");
            }
            else
            {
                SendFeedback("" + message);
            }
        }

        /// <summary>
        /// Lists model folders that contain a loadable main XNB.
        /// </summary>
        public static void PrintAvailableModels()
        {
            string[] names = VehicleContent.GetAvailableModelNames();
            if (names.Length == 0)
            {
                SendFeedback("No selectable models found.");
                SendFeedback("Add folders like !Mods\\DrivableVehicles\\Models\\CyberTruck\\models\\CyberTruck.xnb");
                return;
            }

            SendFeedback("Available models: " + string.Join(", ", names));
            SendFeedback("Select one with: /vehicle model " + names[0]);
        }

        /// <summary>
        /// Prints the selected model path and current load status.
        /// </summary>
        public static void PrintModelStatus()
        {
            string[] summaryLines = VehicleContent.GetActiveModelSummaryLines();

            SendFeedback("Selected model: " + VehicleContent.ActiveModelName);
            SendFeedback("" + VehicleContent.GetActiveModelStatus());
            SendFeedback($"Visual scale={_modelScale:0.###}, yaw offset={MathHelper.ToDegrees(_modelYawOffsetRadians):0.#} degrees, z/up offset={_modelYOffset:0.###}, wheel clone={_cloneMissingWheels}.");

            foreach (string line in summaryLines)
                SendFeedback("" + line);

            foreach (string line in VehicleConfig.GetActiveConfigSummaryLines())
                SendFeedback("" + line);
        }

        /// <summary>
        /// Prints detailed mesh/bone diagnostics for the selected model.
        /// Also writes the same information to !Mods\DrivableVehicles\modeldiag.txt.
        /// </summary>
        public static void PrintModelDiagnostics()
        {
            string[] modelLines = VehicleContent.GetActiveModelDiagnosticLines();
            var combined = new List<string>
            {
                "Selected model: " + VehicleContent.ActiveModelName,
                $"Visual settings: scale={_modelScale:0.###}, yawOffsetDegrees={MathHelper.ToDegrees(_modelYawOffsetRadians):0.#}, zUpOffset={_modelYOffset:0.###}, cloneMissingWheels={_cloneMissingWheels}."
            };
            combined.AddRange(modelLines);
            string[] lines = combined.ToArray();
            string filePath = null;

            try
            {
                filePath = VehicleContent.WriteActiveModelDiagnosticFile(lines);
            }
            catch (Exception ex)
            {
                Log($"Failed to write model diagnostics file: {ex}.");
                SendFeedback("Could not write modeldiag.txt. See the main log for details.");
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (i < 10)
                    SendFeedback("" + lines[i]);

                Log("" + lines[i]);
            }

            if (!string.IsNullOrWhiteSpace(filePath))
                SendFeedback("Model diagnostics written to: " + filePath);
            else if (lines.Length > 10)
                SendFeedback("Full model diagnostics were written to the log.");
        }
        #endregion

        #region Config And Audio Diagnostics

        /// <summary>
        /// Hot-reloads the master config, active vehicle config, and optional WAV sounds.
        /// </summary>
        public static void ReloadConfigs(bool showFeedback)
        {
            try
            {
                VehicleConfig.ReloadAll();
                VehicleAudio.ReloadForActiveModel();

                if (showFeedback)
                {
                    SendFeedback("Reloaded DrivableVehicles.clag, active vehicle.clag, and optional sounds.");
                    SendFeedback("" + string.Join(" ", VehicleConfig.GetActiveConfigSummaryLines()));
                }

                Log("Hot-reloaded vehicle config and sounds.");
            }
            catch (Exception ex)
            {
                Log("Vehicle config reload failed: " + ex + ".");
                if (showFeedback)
                    SendFeedback("Config reload failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Prints optional sound file load status for the selected model.
        /// </summary>
        public static void PrintSoundStatus()
        {
            foreach (string line in VehicleAudio.GetStatusLines())
                SendFeedback("" + line);
        }
        #endregion

        #region Runtime Ticks And Input

        // Note: real keyboard input comes from the HUD postfix path; the regular mod tick is cleanup-only.

        /// <summary>
        /// Per-tick cleanup called by the ModLoader bootstrap.
        /// CastleForge currently passes a null InputManager here, so actual driving
        /// input is handled by TickFromHudInput after the HUD receives real input.
        /// </summary>
        public static void Tick()
        {
            CleanupRemovedVehicles();

            if (CleanHudEnabled)
                ApplyCleanHudAvatarVisibility();

            if (IsDriving)
                AttachPlayerAndCamera(_activeVehicle);
        }

        /// <summary>
        /// Per-HUD-input vehicle runtime. This is called from a Harmony postfix on
        /// InGameHUD.OnPlayerInput so chat and the Escape menu continue to work.
        /// </summary>
        public static void TickFromHudInput(InputManager inputManager, GameTime gameTime)
        {
            CleanupRemovedVehicles();

            if (CleanHudEnabled)
                ApplyCleanHudAvatarVisibility();

            if (inputManager == null || gameTime == null)
                return;

            var keyboard = inputManager.Keyboard;
            VehicleControls controls = VehicleConfig.Controls;

            if (VehicleConfig.WasReloadChordPressed(keyboard))
            {
                ReloadConfigs(true);
                return;
            }

            if (IsDriving)
            {
                if (VehicleConfig.WasKeyPressed(keyboard, controls.ExitKey))
                {
                    ExitVehicle(true);
                    return;
                }

                _activeVehicle.UpdateDriving(inputManager, gameTime);
                AttachPlayerAndCamera(_activeVehicle);
                return;
            }

            if (VehicleConfig.WasKeyPressed(keyboard, controls.EnterKey))
                TryEnterNearest(false);
        }
        #endregion

        #region Vehicle Lifecycle

        // Note: vehicles are temporary scene entities. Clear/spawn/enter/exit do not save to the world yet.

        /// <summary>
        /// Spawns a new placeholder vehicle near the local player.
        /// </summary>
        public static void SpawnVehicle(bool enterAfterSpawn)
        {
            var game = CastleMinerZGame.Instance;
            if (game == null || game.GameScreen == null || game.GameScreen.mainScene == null || game.LocalPlayer == null)
            {
                SendFeedback("You must be in a world before spawning a vehicle.");
                return;
            }

            Vector3 forward = ForwardFrom(game.LocalPlayer.LocalRotation);
            Vector3 spawnPosition = game.LocalPlayer.LocalPosition + forward * 4f;
            spawnPosition.Y += 0.25f;
            spawnPosition = SnapToGround(spawnPosition);

            var vehicle = new PrototypeVehicleEntity
            {
                LocalPosition = spawnPosition,
                Yaw = YawFromQuaternion(game.LocalPlayer.LocalRotation)
            };

            game.GameScreen.mainScene.Children.Add(vehicle);
            _vehicles.Add(vehicle);

            if (VehicleConfig.VehicleStateFeedbackEnabled)
                SendFeedback("Spawned prototype vehicle using model: " + VehicleContent.ActiveModelName + ".");

            if (enterAfterSpawn)
                EnterVehicle(vehicle, true);
        }

        /// <summary>
        /// Attempts to enter the nearest spawned prototype vehicle.
        /// </summary>
        public static bool TryEnterNearest(bool showFeedback)
        {
            var game = CastleMinerZGame.Instance;
            if (game == null || game.LocalPlayer == null)
                return false;

            CleanupRemovedVehicles();

            PrototypeVehicleEntity nearest = null;
            float nearestDistanceSq = EnterDistance * EnterDistance;
            Vector3 playerPos = game.LocalPlayer.LocalPosition;

            for (int i = 0; i < _vehicles.Count; i++)
            {
                PrototypeVehicleEntity vehicle = _vehicles[i];
                if (vehicle == null || vehicle.Parent == null)
                    continue;

                float distanceSq = Vector3.DistanceSquared(playerPos, vehicle.LocalPosition);
                if (distanceSq <= nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    nearest = vehicle;
                }
            }

            if (nearest == null)
            {
                if (showFeedback)
                    SendFeedback("No prototype vehicle is close enough to enter.");
                return false;
            }

            EnterVehicle(nearest, showFeedback);
            return true;
        }

        /// <summary>
        /// Enters the selected prototype vehicle.
        /// </summary>
        public static void EnterVehicle(PrototypeVehicleEntity vehicle, bool showFeedback)
        {
            if (vehicle == null)
                return;

            var game = CastleMinerZGame.Instance;
            var screen = game?.GameScreen;
            if (game == null || screen == null || game.LocalPlayer == null)
                return;

            _activeVehicle = vehicle;
            _activeVehicle.Speed = 0f;
            _changedCameraMode = false;

            BeginVehicleCleanHudOverride();

            VehicleAudio.ReloadForActiveModel();
            VehicleAudio.PlayEnter();

            TryEnableVehicleCamera(screen);

            AttachPlayerAndCamera(_activeVehicle);

            if (ShouldShowVehicleStateFeedback(showFeedback))
                SendFeedback("Entered vehicle. Use configured driving keys; Esc opens menu.");
        }

        /// <summary>
        /// Exits the active prototype vehicle.
        /// </summary>
        public static void ExitVehicle(bool showFeedback)
        {
            if (_activeVehicle == null)
                return;

            var game = CastleMinerZGame.Instance;
            var screen = game?.GameScreen;
            var player = game?.LocalPlayer;
            PrototypeVehicleEntity vehicle = _activeVehicle;

            _activeVehicle = null;
            VehicleAudio.StopDrivingLoops();

            if (player != null)
            {
                Vector3 exit = vehicle.LocalPosition + Vector3.Transform(_exitOffset, Matrix.CreateRotationY(vehicle.Yaw));
                exit = SnapToGround(exit);

                player.LocalPosition = exit;
                player.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.Up, vehicle.Yaw);
                player.PlayerPhysics.WorldVelocity = Vector3.Zero;
            }

            if (screen != null && screen.FreeFlyCameraEnabled && _changedCameraMode)
                TryRestorePlayerCamera(screen);

            EndVehicleCleanHudOverride();

            _changedCameraMode = false;

            if (ShouldShowVehicleStateFeedback(showFeedback))
                SendFeedback("Exited vehicle.");
        }

        /// <summary>
        /// Removes all spawned prototype vehicles.
        /// </summary>
        public static void ClearVehicles(bool showFeedback)
        {
            if (_activeVehicle != null)
                ExitVehicle(false);

            for (int i = 0; i < _vehicles.Count; i++)
            {
                PrototypeVehicleEntity vehicle = _vehicles[i];
                if (vehicle != null && vehicle.Parent != null)
                    vehicle.RemoveFromParent();
            }

            _vehicles.Clear();

            if (ShouldShowVehicleStateFeedback(showFeedback))
                SendFeedback("Cleared prototype vehicles.");
        }
        #endregion

        #region Player Action Suppression

        /// <summary>
        /// Suppresses held-item use side effects while driving.
        /// This prevents mining, shooting, melee, block placement, crack-box progress, and stale construction probes.
        /// </summary>
        public static void SuppressHeldItemUse(InGameHUD hud)
        {
            try
            {
                if (hud != null)
                {
                    hud.ConstructionProbe?.Reset();

                    if (hud.LocalPlayer != null)
                    {
                        hud.LocalPlayer.UsingTool = false;
                        hud.LocalPlayer.Shouldering = false;
                        hud.LocalPlayer.Reloading = false;
                    }

                    if (hud.ActiveInventoryItem != null)
                        hud.ActiveInventoryItem.DigTime = TimeSpan.Zero;
                }

                var game = CastleMinerZGame.Instance;
                if (game != null && game.GameScreen != null && game.GameScreen.CrackBox != null)
                    game.GameScreen.CrackBox.CrackAmount = 0f;
            }
            catch
            {
                // This is a defensive input-suppression helper. Never let it break vehicle driving.
            }
        }
        #endregion

        #region Terrain Helpers

        /// <summary>
        /// Snaps a point down to the nearest solid block surface near the current Y.
        /// </summary>
        internal static Vector3 SnapToGround(Vector3 position)
        {
            try
            {
                var terrain = BlockTerrain.Instance;
                if (terrain == null || !terrain.IsReady)
                    return position;

                int startY = (int)Math.Floor(position.Y) + 3;
                int endY = Math.Max(-63, startY - 18);

                for (int y = startY; y >= endY; y--)
                {
                    BlockTypeEnum block = terrain.GetBlockWithChanges(new Vector3(position.X, y, position.Z));
                    if (BlockType.GetType(block).BlockPlayer)
                    {
                        position.Y = y + 1.05f;
                        return position;
                    }
                }
            }
            catch
            {
                // Terrain may not be fully ready during loading/teleporting.
            }

            return position;
        }
        #endregion

        #region Camera And Seating Helpers

        // Note: free-fly camera use is defensive because CastleMiner Z requires cameras to belong to a Scene before assignment.

        /// <summary>
        /// Safely enables the game's existing free-fly camera for third-person vehicle driving.
        /// CastleMiner Z creates this camera but does not always attach it to a scene before
        /// SwitchFreeFlyCameras assigns it to the main CameraView.
        /// </summary>
        private static void TryEnableVehicleCamera(GameScreen screen)
        {
            try
            {
                if (screen == null || screen.FreeFlyCameraEnabled)
                    return;

                EnsureFreeFlyCameraInScene(screen);
                screen.SwitchFreeFlyCameras();
                _changedCameraMode = true;
            }
            catch (Exception ex)
            {
                _changedCameraMode = false;
                Log($"Vehicle camera switch failed; using player camera instead: {ex.Message}.");
                SendFeedback("Vehicle camera failed, but driving mode is still active.");
            }
        }

        /// <summary>
        /// Restores the normal first-person player camera after leaving the vehicle.
        /// </summary>
        private static void TryRestorePlayerCamera(GameScreen screen)
        {
            try
            {
                if (screen == null || !screen.FreeFlyCameraEnabled)
                    return;

                screen.SwitchFreeFlyCameras();
            }
            catch (Exception ex)
            {
                Log($"Failed to restore player camera: {ex.Message}.");
            }
        }

        /// <summary>
        /// Adds GameScreen.FreeFlyCamera to the main scene before assigning it to CameraView.
        /// CameraView rejects cameras that are not part of a Scene.
        /// </summary>
        private static void EnsureFreeFlyCameraInScene(GameScreen screen)
        {
            if (screen == null || screen.FreeFlyCamera == null || screen.mainScene == null)
                return;

            if (screen.FreeFlyCamera.Scene == null)
                screen.mainScene.Children.Add(screen.FreeFlyCamera);
        }

        /// <summary>
        /// Keeps the local player seated in the active vehicle and positions the free-fly camera behind it.
        /// </summary>
        private static void AttachPlayerAndCamera(PrototypeVehicleEntity vehicle)
        {
            var game = CastleMinerZGame.Instance;
            var screen = game?.GameScreen;
            var player = game?.LocalPlayer;

            if (vehicle == null || player == null)
                return;

            player.LocalPosition = vehicle.GetSeatWorldPosition();
            player.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.Up, vehicle.Yaw);
            player.PlayerPhysics.WorldVelocity = Vector3.Zero;

            if (screen != null && screen.FreeFlyCameraEnabled)
            {
                Vector3 back = Vector3.Transform(Vector3.Backward, Matrix.CreateRotationY(vehicle.Yaw));

                screen.FreeFlyCamera.LocalPosition = vehicle.LocalPosition + back * 7f + new Vector3(0f, 3.2f, 0f);
                screen.FreeFlyCamera.LocalRotation =
                    Quaternion.CreateFromAxisAngle(Vector3.Up, vehicle.Yaw) *
                    Quaternion.CreateFromAxisAngle(Vector3.Right, MathHelper.ToRadians(-13f));
            }
        }
        #endregion

        #region Spawned-Vehicle Maintenance And Math Helpers

        /// <summary>
        /// Reloads the selected model on all currently spawned vehicles and returns how many were refreshed.
        /// </summary>
        private static int RefreshSpawnedVehicleModels()
        {
            int refreshedCount = 0;

            CleanupRemovedVehicles();

            for (int i = 0; i < _vehicles.Count; i++)
            {
                PrototypeVehicleEntity vehicle = _vehicles[i];
                if (vehicle == null || vehicle.Parent == null)
                    continue;

                try
                {
                    vehicle.ReloadSelectedModel();
                    refreshedCount++;
                }
                catch (Exception ex)
                {
                    Log($"Failed to refresh vehicle model: {ex.Message}.");
                }
            }

            return refreshedCount;
        }

        /// <summary>
        /// Removes stale vehicle references and clears driving state if the active vehicle was removed.
        /// </summary>
        private static void CleanupRemovedVehicles()
        {
            for (int i = _vehicles.Count - 1; i >= 0; i--)
            {
                if (_vehicles[i] == null || _vehicles[i].Parent == null)
                    _vehicles.RemoveAt(i);
            }

            if (_activeVehicle != null && _activeVehicle.Parent == null)
            {
                _activeVehicle = null;
                VehicleAudio.StopDrivingLoops();
                EndVehicleCleanHudOverride();
            }
        }

        /// <summary>
        /// Gets a normalized world-forward direction from a rotation, falling back to Vector3.Forward if invalid.
        /// </summary>
        private static Vector3 ForwardFrom(Quaternion rotation)
        {
            Vector3 forward = Vector3.Transform(Vector3.Forward, Matrix.CreateFromQuaternion(rotation));
            if (forward.LengthSquared() < 0.0001f)
                return Vector3.Forward;

            forward.Normalize();
            return forward;
        }

        /// <summary>
        /// Converts a rotation into a horizontal yaw angle in radians.
        /// </summary>
        private static float YawFromQuaternion(Quaternion rotation)
        {
            Vector3 forward = ForwardFrom(rotation);
            return (float)Math.Atan2(-forward.X, -forward.Z);
        }
        #endregion
    }
}