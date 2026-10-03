using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Reflection.Patching;
using BodyDragging.Features;

namespace BodyDragging
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.kobethuy.bodydragging";
        public const string Name = "BodyDragging";
        public const string Version = "1.0.0";

        private const string FikaPluginGuid = "com.fika.core";
        private const string FikaBridgeAssemblyName = "BodyDragFika.dll";
        private const string FikaBridgeTypeName = "BodyDragFika.BodyDragFikaBridge";

        internal static ManualLogSource Log { get; private set; }

        internal static ConfigEntry<bool> Enabled { get; private set; }
        internal static ConfigEntry<float> MaxHandSpeed { get; private set; }
        internal static ConfigEntry<float> HoldSlack { get; private set; }
        internal static ConfigEntry<float> TeleportDistance { get; private set; }
        internal static ConfigEntry<float> MaxHoldError { get; private set; }
        internal static ConfigEntry<float> GrabSpring { get; private set; }
        internal static ConfigEntry<float> MaximumGrabAcceleration { get; private set; }
        internal static ConfigEntry<float> HeldDistanceMultiplier { get; private set; }
        internal static ConfigEntry<float> MaximumSeparationSeconds { get; private set; }
        internal static ConfigEntry<float> LimbFollowStrength { get; private set; }
        internal static ConfigEntry<float> DragSpeedMultiplier { get; private set; }
        internal static ConfigEntry<bool> UnequipHandsWhileDragging { get; private set; }
        internal static ConfigEntry<bool> AllowZombieOrBotCorpses { get; private set; }
        internal static ConfigEntry<bool> DebugLogging { get; private set; }
        internal static ConfigEntry<bool> HeadlessApplyFinalPoseOnly { get; private set; }

        private PatchManager _patchManager;
        private MethodInfo _fikaBridgeShutdown;

        private void Awake()
        {
            Log = Logger;
            BindConfig();
            BodyDragSync.ApplyDragDenied = CorpseDragController.OnDragDenied;
            BodyDragSync.ApplyRemotePose = RemoteCorpseDragFollower.ApplyPose;
            BodyDragSync.ApplyRemoteStop = RemoteCorpseDragFollower.ApplyStop;

            _patchManager = new PatchManager(this, autoPatch: true);
            _patchManager.EnablePatches();

            TryLoadFikaBridge();

            BodyDragLog.Info($"{Name} {Version} loaded");
        }

        private void BindConfig()
        {
            Enabled = Config.Bind("General", "Enable Body Dragging", true,
                "Add DRAG BODY to valid corpse interaction choices.");
            MaxHandSpeed = Config.Bind("Physics", "Max Hand Speed", 4.5f,
                "Fastest the hold point can pull the chest, in m/s. The chest is tethered to it with a small joint, not a spring - this is the only thing that paces the pull.");
            HoldSlack = Config.Bind("Physics", "Hold Slack", 0.05f,
                "How far the chest may drift from the hold point before the joint pulls it back firmly, in meters. Smaller = firmer grip.");
            TeleportDistance = Config.Bind("Physics", "Teleport Distance", 2.5f,
                "A hold point jump further than this in one tick (camera/player snap, vault) moves the whole ragdoll with it instead of letting the joint fling it across the gap.");
            MaxHoldError = Config.Bind("Physics", "Max Hold Error", 2f,
                "If the chest still ends up this far from the hold point (stuck on geometry), the whole ragdoll is relocated there directly.");
            GrabSpring = Config.Bind("Physics", "Grab Spring", 8000f,
                "Base spring strength for the limb-follow assist (Limb Follow Strength below) - the chest itself is now pulled by a tether, not a spring. Higher = limbs track the chest more tightly.");
            MaximumGrabAcceleration = Config.Bind("Physics", "Max Grab Acceleration", 8000f,
                "Clamp on the limb-follow assist's acceleration. Raised alongside Grab Spring so normal catch-up isn't clipped.");
            HeldDistanceMultiplier = Config.Bind("Physics", "Held Distance Multiplier", 0.5f,
                "Fraction of EFT's interaction range used as the held distance.");
            MaximumSeparationSeconds = Config.Bind("Physics", "Max Separation Seconds", 5f,
                "Seconds the corpse may stay outside interaction range before being released.");
            LimbFollowStrength = Config.Bind("Physics", "Limb Follow Strength", 0.5f,
                "Fraction of Grab Spring applied directly to every other ragdoll body (not just the grabbed one), closing the speed gap between torso and limbs. 0 = limbs only follow through joints (old behavior).");
            DragSpeedMultiplier = Config.Bind("General", "Drag Speed Multiplier", 0.2f,
                "Movement speed multiplier applied to the local player while dragging.");
            UnequipHandsWhileDragging = Config.Bind("General", "Unequip Hands While Dragging", true,
                "Empty the local player's hands for the duration of the drag, restoring the previous item afterward.");
            AllowZombieOrBotCorpses = Config.Bind("General", "Allow Zombie/Bot Corpses", true,
                "Allow dragging zombie and bot corpses, not just other players.");
            DebugLogging = Config.Bind("Debug", "Debug Logging", false,
                "Verbose logging for body-drag state changes.");
            HeadlessApplyFinalPoseOnly = Config.Bind("Fika", "Headless Applies Final Pose Only", true,
                "On a headless host, skip applying continuous drag poses (no one is watching) and only snap to the final pose when the drag stops, so AI dead-body memory and corpse position stay correct.");
        }

        // BodyDragFika.dll ships beside this DLL but is never referenced by this assembly, so a
        // solo install never resolves Fika.Core and never shows a "1 plugin failed to load"
        // error. Loaded only when Fika itself is present in the chainloader.
        private void TryLoadFikaBridge()
        {
            if (!Chainloader.PluginInfos.ContainsKey(FikaPluginGuid))
                return;

            try
            {
                string bridgePath = Path.Combine(
                    Path.GetDirectoryName(Info.Location) ?? string.Empty,
                    FikaBridgeAssemblyName);
                if (!File.Exists(bridgePath))
                {
                    BodyDragLog.Warning($"[Fika] Fika detected but {FikaBridgeAssemblyName} is missing, raids will run unsynced");
                    return;
                }

                Assembly bridgeAssembly = Assembly.LoadFrom(bridgePath);
                Type bridgeType = bridgeAssembly.GetType(FikaBridgeTypeName);
                MethodInfo initialize = bridgeType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                _fikaBridgeShutdown = bridgeType?.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static);
                if (initialize == null)
                {
                    BodyDragLog.Warning("[Fika] Bridge assembly found but entry point is missing");
                    return;
                }

                initialize.Invoke(null, new object[] { Log });
                BodyDragLog.Info("[Fika] Bridge attached");
            }
            catch (Exception exception)
            {
                BodyDragLog.Error($"[Fika] Could not load bridge, raids will run unsynced: {exception}");
            }
        }

        private void Update()
        {
            BodyDragSync.Tick?.Invoke();
            RemoteCorpseDragFollower.Tick();
        }

        private void OnDestroy()
        {
            try
            {
                _fikaBridgeShutdown?.Invoke(null, null);
            }
            catch (Exception exception)
            {
                BodyDragLog.Error($"[Fika] Bridge shutdown failed: {exception}");
            }

            _patchManager?.DisablePatches();
            Log = null;
        }
    }
}
