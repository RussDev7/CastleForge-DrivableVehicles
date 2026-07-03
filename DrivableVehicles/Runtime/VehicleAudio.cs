/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7
This file is part of https://github.com/RussDev7/CastleForge - see LICENSE for details.

DrivableVehicles prototype changes:
- Adds optional WAV playback for enter, accelerate, decelerate, and skid/drift sounds.
- Missing sound files are skipped without breaking driving.
*/

using Microsoft.Xna.Framework.Audio;
using System.Collections.Generic;
using System.IO;
using System;

using static ModLoader.LogSystem;

namespace DrivableVehicles
{
    /// <summary>
    /// Small WAV sound layer for prototype vehicle driving.
    /// 
    /// This intentionally uses loose WAV files instead of XACT banks so sounds can be edited
    /// and hot-reloaded from !Mods without rebuilding game content.
    /// </summary>
    internal static class VehicleAudio
    {
        #region Runtime State

        // Tracks active one-shot sound instances so they can be disposed after playback.
        private static readonly List<SoundEffectInstance> _oneShots = new List<SoundEffectInstance>();

        // Cached model/settings pair used to detect when the selected vehicle changed.
        private static string _loadedModelName;
        private static VehicleSettings _settings;

        #endregion

        #region Loaded Sound Effects

        // Optional WAV assets loaded for the currently selected vehicle.
        private static SoundEffect _enterSound;
        private static SoundEffect _accelerateSound;
        private static SoundEffect _decelerateSound;
        private static SoundEffect _skidSound;

        #endregion

        #region Loop Instances

        // Reusable loop instances for continuous driving sounds.
        private static SoundEffectInstance _accelerateLoop;
        private static SoundEffectInstance _decelerateLoop;
        private static SoundEffectInstance _skidLoop;

        #endregion

        #region Public Lifecycle

        /// <summary>
        /// Reloads optional WAV sounds for the currently selected vehicle model.
        /// </summary>
        public static void ReloadForActiveModel()
        {
            StopDrivingLoops();
            DisposeLoadedSounds();

            _loadedModelName = VehicleContent.ActiveModelName;
            _settings = VehicleConfig.GetActiveVehicleSettings();

            if (_settings == null || !_settings.SoundsEnabled)
                return;

            _enterSound = LoadOptional(_settings, _settings.EnterSoundPath, "enter");
            _accelerateSound = LoadOptional(_settings, _settings.AccelerateSoundPath, "accelerate");
            _decelerateSound = LoadOptional(_settings, _settings.DecelerateSoundPath, "decelerate");
            _skidSound = LoadOptional(_settings, _settings.SkidSoundPath, "skid");

            _accelerateLoop = CreateLoop(_accelerateSound);
            _decelerateLoop = CreateLoop(_decelerateSound);
            _skidLoop = CreateLoop(_skidSound);
        }

        /// <summary>
        /// Stops and disposes all loaded sounds during shutdown.
        /// </summary>
        public static void Reset()
        {
            StopDrivingLoops();
            DisposeLoadedSounds();
            _loadedModelName = null;
            _settings = null;
        }
        #endregion

        #region Public Playback

        /// <summary>
        /// Plays the optional enter-car one-shot.
        /// </summary>
        public static void PlayEnter()
        {
            EnsureActiveSoundsLoaded();

            if (_enterSound == null || _settings == null || !_settings.SoundsEnabled)
                return;

            try
            {
                SoundEffectInstance instance = _enterSound.CreateInstance();
                instance.Volume = _settings.SoundVolume;
                instance.Play();
                _oneShots.Add(instance);
            }
            catch (Exception ex)
            {
                Log("Failed to play enter sound: " + ex.Message + ".");
            }
        }

        /// <summary>
        /// Updates optional looped driving sounds.
        /// </summary>
        public static void UpdateDriving(bool forward, bool reverse, bool braking, bool steering, float speed)
        {
            EnsureActiveSoundsLoaded();
            CleanupOneShots();

            if (_settings == null || !_settings.SoundsEnabled)
            {
                StopDrivingLoops();
                return;
            }

            bool moving = Math.Abs(speed) > 0.35f;
            bool skidding = braking && forward && steering && moving;
            bool accelerating = forward && !braking;
            bool decelerating = !skidding && ((braking && moving) || reverse || (!forward && moving));

            SetLoopPlaying(_accelerateLoop, accelerating);
            SetLoopPlaying(_decelerateLoop, decelerating);
            SetLoopPlaying(_skidLoop, skidding);
        }

        /// <summary>
        /// Stops all looped driving sounds.
        /// </summary>
        public static void StopDrivingLoops()
        {
            StopLoop(_accelerateLoop);
            StopLoop(_decelerateLoop);
            StopLoop(_skidLoop);
            CleanupOneShots();
        }
        #endregion

        #region Public Diagnostics

        /// <summary>
        /// Returns a short sound status summary for chat/log output.
        /// </summary>
        public static string[] GetStatusLines()
        {
            EnsureActiveSoundsLoaded();

            VehicleSettings settings = VehicleConfig.GetActiveVehicleSettings();
            return new[]
            {
                "Sound config: " + (settings == null ? "<none>" : (settings.SoundsEnabled ? "enabled" : "disabled")) +
                    ", volume=" + (settings == null ? "n/a" : settings.SoundVolume.ToString("0.##")) + ".",
                "Enter sound: " + DescribeSound(settings, settings?.EnterSoundPath, _enterSound),
                "Accelerate sound: " + DescribeSound(settings, settings?.AccelerateSoundPath, _accelerateSound),
                "Decelerate sound: " + DescribeSound(settings, settings?.DecelerateSoundPath, _decelerateSound),
                "Skid sound: " + DescribeSound(settings, settings?.SkidSoundPath, _skidSound)
            };
        }
        #endregion

        #region Load Helpers

        /// <summary>
        /// Reloads vehicle sounds when the active model changes.
        /// </summary>
        private static void EnsureActiveSoundsLoaded()
        {
            if (!string.Equals(_loadedModelName, VehicleContent.ActiveModelName, StringComparison.OrdinalIgnoreCase))
                ReloadForActiveModel();
        }

        /// <summary>
        /// Loads an optional WAV file from the active vehicle config, returning null when missing or invalid.
        /// </summary>
        private static SoundEffect LoadOptional(VehicleSettings settings, string configuredPath, string label)
        {
            if (settings == null || string.IsNullOrWhiteSpace(configuredPath))
                return null;

            string path = VehicleConfig.ResolveVehiclePath(settings, configuredPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log("Optional vehicle " + label + " sound missing; skipping: " + configuredPath + ".");
                return null;
            }

            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    SoundEffect sound = SoundEffect.FromStream(stream);
                    Log("Loaded vehicle " + label + " sound: " + path + ".");
                    return sound;
                }
            }
            catch (Exception ex)
            {
                Log("Failed to load vehicle " + label + " sound '" + path + "': " + ex.Message + ".");
                return null;
            }
        }

        /// <summary>
        /// Creates a looped sound instance for a loaded sound effect.
        /// </summary>
        private static SoundEffectInstance CreateLoop(SoundEffect sound)
        {
            if (sound == null)
                return null;

            try
            {
                SoundEffectInstance instance = sound.CreateInstance();
                instance.IsLooped = true;
                instance.Volume = _settings == null ? 0.75f : _settings.SoundVolume;
                return instance;
            }
            catch (Exception ex)
            {
                Log("Failed to create vehicle sound loop: " + ex.Message + ".");
                return null;
            }
        }
        #endregion

        #region Loop Control

        /// <summary>
        /// Starts or stops a loop instance based on the current driving state.
        /// </summary>
        private static void SetLoopPlaying(SoundEffectInstance instance, bool shouldPlay)
        {
            if (instance == null)
                return;

            try
            {
                if (_settings != null)
                    instance.Volume = _settings.SoundVolume;

                if (shouldPlay)
                {
                    if (instance.State != SoundState.Playing)
                        instance.Play();
                }
                else
                {
                    if (instance.State == SoundState.Playing)
                        instance.Stop();
                }
            }
            catch (Exception ex)
            {
                Log("Failed to update vehicle sound loop: " + ex.Message + ".");
            }
        }

        /// <summary>
        /// Stops a loop instance if it is currently playing or paused.
        /// </summary>
        private static void StopLoop(SoundEffectInstance instance)
        {
            if (instance == null)
                return;

            try
            {
                if (instance.State == SoundState.Playing || instance.State == SoundState.Paused)
                    instance.Stop();
            }
            catch
            {
            }
        }
        #endregion

        #region Cleanup Helpers

        /// <summary>
        /// Disposes finished one-shot sound instances and removes dead entries.
        /// </summary>
        private static void CleanupOneShots()
        {
            for (int i = _oneShots.Count - 1; i >= 0; i--)
            {
                SoundEffectInstance instance = _oneShots[i];
                if (instance == null)
                {
                    _oneShots.RemoveAt(i);
                    continue;
                }

                try
                {
                    if (instance.State == SoundState.Stopped)
                    {
                        instance.Dispose();
                        _oneShots.RemoveAt(i);
                    }
                }
                catch
                {
                    try { instance.Dispose(); } catch { }
                    _oneShots.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Disposes all loop instances, one-shots, and loaded sound effects.
        /// </summary>
        private static void DisposeLoadedSounds()
        {
            DisposeInstance(ref _accelerateLoop);
            DisposeInstance(ref _decelerateLoop);
            DisposeInstance(ref _skidLoop);

            for (int i = 0; i < _oneShots.Count; i++)
            {
                try { _oneShots[i]?.Dispose(); } catch { }
            }
            _oneShots.Clear();

            DisposeSound(ref _enterSound);
            DisposeSound(ref _accelerateSound);
            DisposeSound(ref _decelerateSound);
            DisposeSound(ref _skidSound);
        }

        /// <summary>
        /// Stops, disposes, and clears a single sound instance reference.
        /// </summary>
        private static void DisposeInstance(ref SoundEffectInstance instance)
        {
            if (instance == null)
                return;

            try { instance.Stop(); } catch { }
            try { instance.Dispose(); } catch { }
            instance = null;
        }

        /// <summary>
        /// Disposes and clears a single loaded sound effect reference.
        /// </summary>
        private static void DisposeSound(ref SoundEffect sound)
        {
            if (sound == null)
                return;

            try { sound.Dispose(); } catch { }
            sound = null;
        }
        #endregion

        #region Diagnostics Helpers

        /// <summary>
        /// Describes whether a configured sound is loaded, missing, or present but unavailable.
        /// </summary>
        private static string DescribeSound(VehicleSettings settings, string configuredPath, SoundEffect loaded)
        {
            if (settings == null || string.IsNullOrWhiteSpace(configuredPath))
                return "<none>";

            string resolved = VehicleConfig.ResolveVehiclePath(settings, configuredPath);
            if (loaded != null)
                return "loaded (" + resolved + ")";

            return File.Exists(resolved) ? "present but not loaded (" + resolved + ")" : "missing (" + configuredPath + ")";
        }
        #endregion
    }
}