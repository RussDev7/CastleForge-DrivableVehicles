/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7
This file is part of https://github.com/RussDev7/CastleForge - see LICENSE for details.

DrivableVehicles prototype changes:
- Adds a tiny local-only vehicle testbed.
- Uses a procedural placeholder car so the gameplay loop can be tested before Unity vehicle meshes are converted to XNB.
- v0.1.1 fixes the vehicle camera handoff by attaching the free-fly camera to the main scene before switching.
- v0.1.2 moves driving input into the HUD input postfix so chat and Esc keep working.
- v0.1.3 loads an optional Truck.xnb model from !Mods\DrivableVehicles\Models\Truck.
- v0.1.4 defaults to the tested scale/yaw and adds model diagnostics for partial imports.
- v0.1.5 writes /vehicle modeldiag output to !Mods\DrivableVehicles\modeldiag.txt.
- v0.1.6 adds /vehicle zoffset for moving the loaded model up/down without re-exporting.
- v0.1.7 defaults zoffset to 0.5 and clones Wheel_1 at missing wheel bones.
- v0.1.8 lists/selects model folders under !Mods\DrivableVehicles\Models.
- v0.1.9 makes missing-wheel cloning work with Blender suffixes like Wheel_1.002.
- v0.1.10 lets /vehicle model and /vehicle selectmodel accept folder names with spaces.
- v0.1.11 lets the original procedural placeholder be selected as Alpha Prototype.
- v0.1.12 lets real XNB model folders take priority over Alpha Prototype aliases.
- v0.1.13 adds /vehicle cleanhud to hide the player model, build selection, and main HUD while keeping distance and chat.
- v0.1.14 automatically enables clean HUD while driving and blocks held-item actions from the vehicle.
- v0.1.15 adds .clag configs, optional WAV sounds, hot-reload, per-vehicle speeds, and /veh alias.
- v0.1.16 supports per-vehicle models subfolders and stops creating a top-level Sounds folder.
- v0.1.17 changes generated/default Enter and Exit keys to R.
- v0.1.19 shortens model-selection feedback to a single chat line.
- v0.1.20 adds concise command/help summaries that match the vehicle workflow.
*/

using Microsoft.Xna.Framework;
using System.Globalization;
using System.Reflection;
using DNA.CastleMinerZ;
using ModLoaderExt;
using DNA.Input;
using ModLoader;
using System.IO;
using System;

using static ModLoader.LogSystem;

namespace DrivableVehicles
{
    /// <summary>
    /// Prototype drivable vehicle mod.
    /// 
    /// This intentionally starts small:
    /// - Chat commands spawn/enter/exit/clear vehicles.
    /// - A selected XNB model is drawn when present; otherwise a procedural placeholder vehicle is drawn.
    /// - Driving is local-only and does not yet save or sync over multiplayer.
    /// </summary>
    [Priority(Priority.Normal)]
    [RequiredDependencies("ModLoaderExtensions")]
    public class DrivableVehicles : ModBase
    {
        #region Mod Initiation

        private readonly CommandDispatcher _dispatcher;

        /// <summary>
        /// Entrypoint for the DrivableVehicles prototype.
        /// </summary>
        public DrivableVehicles() : base("DrivableVehicles", new Version("0.1.20.0"))
        {
            EmbeddedResolver.Init();
            _dispatcher = new CommandDispatcher(this);

            var game = CastleMinerZGame.Instance;
            if (game != null)
                game.Exiting += (s, e) => Shutdown();
        }

        /// <summary>
        /// Called once when the mod is first loaded by the ModLoader.
        /// </summary>
        public override void Start()
        {
            var game = CastleMinerZGame.Instance;
            if (game == null)
            {
                Log("Game instance is null.");
                return;
            }

            var ns    = typeof(DrivableVehicles).Namespace;
            var dest  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "!Mods", ns);
            var wrote = EmbeddedExporter.ExtractFolder(ns, dest);
            if (wrote > 0) Log($"Extracted {wrote} file(s) to {dest}.");

            VehicleConfig.Initialize();
            VehicleAudio.ReloadForActiveModel();

            GamePatches.ApplyAllPatches();

            ChatInterceptor.RegisterHandler(raw => _dispatcher.TryInvoke(raw));
            HelpRegistry.Register(this.Name, commands);

            Log($"{MethodBase.GetCurrentMethod().DeclaringType.Namespace} loaded.");
        }

        /// <summary>
        /// Called when the game exits or the mod is unloaded.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                try { VehicleRuntime.DisableCleanHudSilently(); } catch (Exception ex) { Log($"Clean HUD cleanup failed: {ex.Message}."); }
                try { VehicleRuntime.ClearVehicles(false); } catch (Exception ex) { Log($"Vehicle cleanup failed: {ex.Message}."); }
                try { VehicleAudio.Reset(); } catch (Exception ex) { Log($"Audio cleanup failed: {ex.Message}."); }
                try { VehicleContent.Reset(); } catch (Exception ex) { Log($"Content cleanup failed: {ex.Message}."); }
                try { GamePatches.DisableAll(); } catch (Exception ex) { Log($"Disable hooks failed: {ex.Message}."); }

                Log($"{MethodBase.GetCurrentMethod().DeclaringType.Namespace} shutdown complete.");
            }
            catch (Exception ex)
            {
                Log($"Error shutting down mod: {ex}.");
            }
        }

        /// <summary>
        /// Called once per game tick.
        /// Handles local vehicle input after the vehicle is spawned/entered.
        /// </summary>
        public override void Tick(InputManager inputManager, GameTime gameTime)
        {
            VehicleRuntime.Tick();
        }

        #endregion

        #region Chat Command Functions

        /// <summary>
        /// HelpRegistry entries for the vehicle command flow. Descriptions are intentionally short
        /// so they are readable in the in-game help list.
        /// </summary>
        private static readonly (string command, string description)[] commands = new (string, string)[]
        {
            ("vehicle spawn", "Flow: spawn a vehicle and auto-enter it."),
            ("vehicle enter", "Flow: enter the nearest spawned vehicle."),
            ("vehicle exit",  "Flow: leave the current vehicle."),
            ("vehicle clear", "Flow: remove spawned prototype vehicles."),
            ("vehicle models", "Models: list selectable model folders."),
            ("vehicle model", "Models: show the selected model and load status."),
            ("vehicle model <name>", "Models: select an XNB model folder; spaced names are supported."),
            ("vehicle modeldiag", "Models: write mesh/bone diagnostics to modeldiag.txt."),
            ("vehicle scale <number>", "Tuning: resize the visual model."),
            ("vehicle yawoffset <degrees>", "Tuning: rotate the visual model if it faces sideways."),
            ("vehicle zoffset <number>", "Tuning: move the visual model up/down; Blender Z maps to game Y."),
            ("vehicle wheelclone <on|off>", "Tuning: clone the imported wheel mesh onto missing wheel bones."),
            ("vehicle cleanhud (on|off|toggle)", "HUD: toggle clean HUD; driving still enables it automatically."),
            ("vehicle sounds", "Audio: show optional WAV sound load status."),
            ("vehicle reload", "Config: hot-reload master config, vehicle config, and sounds."),
            ("vehicle config", "Config: show config paths, model path, and reload shortcut."),
            ("DrivableVehicles.clag [Feedback] VehicleState=false", "Config: hide routine spawned/entered/exited/cleared messages."),
            ("veh", "Alias: shortcut for /vehicle."),
            ("vehicle help",  "Help: show the concise vehicle command flow.")
        };

        /// <summary>
        /// Handles /vehicle commands.
        /// </summary>
        [Command("/vehicle")]
        private static void ExecuteVehicle(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    PrintVehicleHelp();
                    return;
                }

                switch (args[0].ToLowerInvariant())
                {
                    case "spawn":
                        VehicleRuntime.SpawnVehicle(true);
                        break;

                    case "enter":
                        VehicleRuntime.TryEnterNearest(true);
                        break;

                    case "exit":
                        VehicleRuntime.ExitVehicle(true);
                        break;

                    case "clear":
                        VehicleRuntime.ClearVehicles(true);
                        break;

                    case "models":
                    case "listmodels":
                        VehicleRuntime.PrintAvailableModels();
                        break;

                    case "model":
                        if (args.Length >= 2)
                            VehicleRuntime.SelectModel(JoinArgs(args, 1));
                        else
                            VehicleRuntime.PrintModelStatus();
                        break;

                    case "selectmodel":
                        if (args.Length >= 2)
                            VehicleRuntime.SelectModel(JoinArgs(args, 1));
                        else
                            SendFeedback("Usage: /vehicle model Truck");
                        break;

                    case "modeldiag":
                        VehicleRuntime.PrintModelDiagnostics();
                        break;

                    case "scale":
                        SetModelScale(args);
                        break;

                    case "yawoffset":
                        SetModelYawOffset(args);
                        break;

                    case "zoffset":
                    case "modelz":
                    case "height":
                        SetModelZOffset(args);
                        break;

                    case "wheelclone":
                    case "clonewheels":
                    case "missingwheels":
                        SetWheelClone(args);
                        break;

                    case "cleanhud":
                    case "minimalhud":
                    case "cinematichud":
                    case "vehiclehud":
                        SetCleanHud(args);
                        break;

                    case "sounds":
                    case "sound":
                    case "audio":
                        VehicleRuntime.PrintSoundStatus();
                        break;

                    case "reload":
                    case "hotreload":
                    case "configreload":
                        VehicleRuntime.ReloadConfigs(true);
                        break;

                    case "config":
                    case "configpaths":
                    case "paths":
                        PrintConfigPaths();
                        break;

                    case "help":
                    default:
                        PrintVehicleHelp();
                        break;
                }
            }
            catch (Exception ex)
            {
                SendFeedback($"ERROR: {ex.Message}");
                Log($"Command failed: {ex}.");
            }
        }

        /// <summary>
        /// Handles /veh shortcut commands.
        /// </summary>
        [Command("/veh")]
        private static void ExecuteVeh(string[] args)
        {
            ExecuteVehicle(args);
        }

        /// <summary>
        /// Joins remaining command arguments so model folders with spaces, such as "tofu machine",
        /// can be selected from chat commands.
        /// </summary>
        private static string JoinArgs(string[] args, int startIndex)
        {
            if (args == null || startIndex >= args.Length)
                return string.Empty;

            return string.Join(" ", args, startIndex, args.Length - startIndex).Trim().Trim('"', '\'');
        }


        /// <summary>
        /// Parses and applies the visual model scale debug setting.
        /// </summary>
        private static void SetModelScale(string[] args)
        {
            if (args.Length < 2)
            {
                SendFeedback("Usage: /vehicle scale 10");
                return;
            }

            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float scale))
            {
                SendFeedback("Could not parse model scale. Example: /vehicle scale 10");
                return;
            }

            VehicleRuntime.SetModelScale(scale);
        }

        /// <summary>
        /// Parses and applies the visual yaw offset for models that face sideways after export.
        /// </summary>
        private static void SetModelYawOffset(string[] args)
        {
            if (args.Length < 2)
            {
                SendFeedback("Usage: /vehicle yawoffset 90");
                return;
            }

            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float degrees))
            {
                SendFeedback("Could not parse yaw offset. Example: /vehicle yawoffset 90");
                return;
            }

            VehicleRuntime.SetModelYawOffsetDegrees(degrees);
        }

        /// <summary>
        /// Parses and applies the visual height offset. Blender Z maps to CastleMiner Z world Y.
        /// </summary>
        private static void SetModelZOffset(string[] args)
        {
            if (args.Length < 2)
            {
                SendFeedback("Usage: /vehicle zoffset 0.5");
                return;
            }

            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float offset))
            {
                SendFeedback("Could not parse vertical offset. Example: /vehicle zoffset 0.5");
                return;
            }

            VehicleRuntime.SetModelYOffset(offset);
        }


        /// <summary>
        /// Parses and applies the missing-wheel clone workaround toggle.
        /// </summary>
        private static void SetWheelClone(string[] args)
        {
            if (args.Length < 2)
            {
                SendFeedback("Usage: /vehicle wheelclone on");
                return;
            }

            string value = args[1].ToLowerInvariant();
            if (value == "on" || value == "true" || value == "1" || value == "yes")
            {
                VehicleRuntime.SetCloneMissingWheels(true);
                return;
            }

            if (value == "off" || value == "false" || value == "0" || value == "no")
            {
                VehicleRuntime.SetCloneMissingWheels(false);
                return;
            }

            SendFeedback("Could not parse wheel clone setting. Use: /vehicle wheelclone on|off");
        }

        /// <summary>
        /// Parses and applies the manual clean-HUD preference. Vehicle entry temporarily enables
        /// clean HUD regardless of this preference and restores it on exit.
        /// </summary>
        private static void SetCleanHud(string[] args)
        {
            if (args.Length < 2)
            {
                VehicleRuntime.SetCleanHudEnabled(!VehicleRuntime.ManualCleanHudEnabled);
                return;
            }

            string value = args[1].ToLowerInvariant();
            if (value == "toggle")
            {
                VehicleRuntime.SetCleanHudEnabled(!VehicleRuntime.ManualCleanHudEnabled);
                return;
            }

            if (value == "status" || value == "check")
            {
                SendFeedback("Clean HUD is " + (VehicleRuntime.CleanHudEnabled ? "enabled." : "disabled.") + " Manual preference is " + (VehicleRuntime.ManualCleanHudEnabled ? "enabled." : "disabled.") + " Vehicle override is " + (VehicleRuntime.IsDriving ? "active." : "inactive."));
                return;
            }

            if (value == "on" || value == "true" || value == "1" || value == "yes")
            {
                VehicleRuntime.SetCleanHudEnabled(true);
                return;
            }

            if (value == "off" || value == "false" || value == "0" || value == "no")
            {
                VehicleRuntime.SetCleanHudEnabled(false);
                return;
            }

            SendFeedback("Could not parse clean HUD setting. Use: /vehicle cleanhud on|off|toggle");
        }

        /// <summary>
        /// Prints the master config, template config, active vehicle config, and selected model path.
        /// </summary>
        private static void PrintConfigPaths()
        {
            string master = VehicleConfig.MasterConfigPath;
            string template = Path.Combine(VehicleConfig.ModRoot, VehicleConfig.VehicleConfigTemplateFileName);
            string activeVehicle = Path.Combine(VehicleContent.GetModelFolderPath(VehicleContent.ActiveModelName), VehicleConfig.VehicleConfigFileName);

            SendFeedback("Master config: " + master + (File.Exists(master) ? " (exists)" : " (missing; run /vehicle reload to create)"));
            SendFeedback("Vehicle config template: " + template + (File.Exists(template) ? " (exists)" : " (missing; run /vehicle reload to create)"));
            SendFeedback("Active vehicle config: " + activeVehicle + (File.Exists(activeVehicle) ? " (exists)" : " (missing; using defaults)"));
            SendFeedback("Model XNB path: " + VehicleContent.GetActiveModelPath());
            SendFeedback("Hot reload: Ctrl+Shift+R by default, or /vehicle reload.");
        }

        /// <summary>
        /// Prints a short command-flow summary for the most common vehicle tasks.
        /// </summary>
        private static void PrintVehicleHelp()
        {
            SendFeedback("Flow: /veh spawn, /veh enter, /veh exit, /veh clear.");
            SendFeedback("Models: /veh models, /veh model <name>, /veh modeldiag.");
            SendFeedback("Tuning: /veh scale 10, /veh yawoffset 90, /veh zoffset 0.5, /veh wheelclone on.");
            SendFeedback("HUD/audio: clean HUD auto-enables while driving; /veh cleanhud and /veh sounds are available.");
            SendFeedback("Config: /veh config, /veh reload, or Ctrl+Shift+R hot-reload configs and sounds.");
            SendFeedback("Defaults: W/S throttle/reverse, A/D steer, Space brake, R enter/exit. Held-item actions are blocked while driving.");
            SendFeedback("Tip: /veh is the shortcut alias for /vehicle, and model names with spaces are supported.");
        }
        #endregion
    }
}