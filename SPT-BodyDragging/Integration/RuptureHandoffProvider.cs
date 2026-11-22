using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT.Interactive;
using UnityEngine;

namespace BodyDragging.Integration
{
    // Values mirror Rupture.Integration.HandoffResult / HandoffState (ABI V1; the numbers are the contract).
    internal enum HandoffCode { Ok = 0, Pending = 1, NotManaged = 2, Busy = 3, Unavailable = 4, InvalidArgument = 5, InvalidToken = 6, Failed = 7, Stale = 8 }
    internal enum HandoffState { Unmanaged = 0, Preparing = 1, Managed = 2, Released = 3, Unavailable = 4 }

    internal sealed class HandoffInfo
    {
        internal HandoffState State;
        internal bool Simulating;
        internal bool WeaponIndependent;
        internal uint DeathSequence, OwnershipEpoch;
        internal Transform[] SeveredRoots = Array.Empty<Transform>();
    }

    // Optional adapter for Rupture.Integration.CorpseHandoffV1 (Rupture 1.4.0+): Rupture lets go of a corpse on
    // this peer so BodyDragging can simulate it in its own Unity physics, and rebuilds its rig from the final pose
    // when the body is handed back. No assembly reference to Rupture; absent or incompatible = Available false and
    // the caller keeps using CorpseDragV1 / the standalone route.
    internal static class RuptureHandoffProvider
    {
        private static MethodInfo _inspect, _release, _renew, _adopt;
        private static Type _releaseType;
        private static readonly Dictionary<string, FieldInfo> InfoFields = new();
        private static FieldInfo _timeoutField;
        private static bool _broken;

        internal static bool Available { get; private set; }

        internal static void Initialize()
        {
            Available = false;
            if (!Chainloader.PluginInfos.TryGetValue(RuptureDragProvider.PluginGuid, out var plugin))
                return;
            Assembly assembly = plugin.Instance.GetType().Assembly;
            Type api = assembly.GetType("Rupture.Integration.CorpseHandoffV1", false);
            if (api == null)
            {
                BodyDragLog.Info("[Rupture] Corpse handoff API not present (Rupture older than 1.4.0), handoff dragging off");
                return;
            }
            Bind(assembly, api);
        }

        // api: the static class carrying AbiVersion/Inspect/Release/Renew/Adopt (CorpseHandoffV1, or a test facade)
        internal static void Bind(Assembly assembly, Type api)
        {
            Available = false; _broken = false;
            try
            {
                if ((int)api.GetProperty("AbiVersion").GetValue(null) != 1)
                    throw new NotSupportedException("requires corpse handoff ABI V1");
                _inspect = api.GetMethod("Inspect"); _release = api.GetMethod("Release");
                _renew = api.GetMethod("Renew"); _adopt = api.GetMethod("Adopt");
                if (_inspect == null || _release == null || _renew == null || _adopt == null)
                    throw new MissingMethodException("incomplete corpse handoff ABI");
                Type info = assembly.GetType("Rupture.Integration.CorpseHandoffInfo", true);
                foreach (string name in new[] { "State", "Simulating", "DeathSequence", "OwnershipEpoch", "SeveredRoots", "WeaponIndependent" })
                    InfoFields[name] = info.GetField(name, BindingFlags.Public | BindingFlags.Instance)
                        ?? throw new MissingFieldException(info.Name, name);
                _releaseType = assembly.GetType("Rupture.Integration.HandoffRelease", true);
                _timeoutField = _releaseType.GetField("LeaseTimeout", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingFieldException(_releaseType.Name, "LeaseTimeout");
                Available = true;
                BodyDragLog.Info("[Rupture] Corpse handoff ABI V1 available");
            }
            catch (Exception exception)
            {
                BodyDragLog.Warning("[Rupture] Handoff dragging unavailable: " + exception.GetBaseException().Message);
                _broken = true;
            }
        }

        private static HandoffCode Call(MethodInfo method, object[] args)
        {
            if (_broken || method == null) return HandoffCode.Failed;
            try { return (HandoffCode)Convert.ToInt32(method.Invoke(null, args)); }
            catch (Exception exception)
            {
                BodyDragLog.Warning("[Rupture] Handoff call failed, handoff dragging disabled: " + exception.GetBaseException().Message);
                _broken = true; Available = false;
                return HandoffCode.Failed;
            }
        }

        internal static HandoffCode Inspect(Corpse corpse, out HandoffInfo info)
        {
            info = null;
            if (!Available) return HandoffCode.Failed;
            object[] args = { corpse, null };
            HandoffCode code = Call(_inspect, args);
            if (args[1] == null) return code;
            try
            {
                object raw = args[1];
                info = new HandoffInfo
                {
                    State = (HandoffState)Convert.ToInt32(InfoFields["State"].GetValue(raw)),
                    Simulating = (bool)InfoFields["Simulating"].GetValue(raw),
                    WeaponIndependent = (bool)InfoFields["WeaponIndependent"].GetValue(raw),
                    DeathSequence = (uint)InfoFields["DeathSequence"].GetValue(raw),
                    OwnershipEpoch = (uint)InfoFields["OwnershipEpoch"].GetValue(raw),
                    SeveredRoots = (Transform[])InfoFields["SeveredRoots"].GetValue(raw) ?? Array.Empty<Transform>()
                };
            }
            catch (Exception exception)
            {
                BodyDragLog.Warning("[Rupture] Handoff info unreadable, handoff dragging disabled: " + exception.GetBaseException().Message);
                _broken = true; Available = false; info = null;
                return HandoffCode.Failed;
            }
            return code;
        }

        internal static HandoffCode Release(Corpse corpse, float leaseSeconds, out ulong token)
        {
            token = 0;
            if (!Available) return HandoffCode.Failed;
            object request = Activator.CreateInstance(_releaseType);
            _timeoutField.SetValue(request, leaseSeconds);
            object[] args = { corpse, request, 0UL };
            HandoffCode code = Call(_release, args);
            token = (ulong)args[2];
            return code;
        }

        internal static HandoffCode Renew(ulong token) => Available ? Call(_renew, new object[] { token }) : HandoffCode.Failed;
        internal static HandoffCode Adopt(ulong token) => Available ? Call(_adopt, new object[] { token }) : HandoffCode.Failed;
    }
}
