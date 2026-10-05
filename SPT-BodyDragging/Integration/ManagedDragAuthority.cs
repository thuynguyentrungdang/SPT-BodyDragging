using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace BodyDragging.Integration
{
    // Plugin.Update drives this on solo and graphical/headless Fika authorities.
    // There is deliberately no local-player, camera or Unity rigidbody mutation.
    internal static class ManagedDragAuthority
    {
        private sealed class Session
        {
            internal ManagedDragStart Identity;
            internal Corpse Corpse;
            internal CorpseRagdoll Ragdoll;
            internal ulong Lease;
            internal ManagedDragSettings Settings;
            internal readonly ManagedIntentBuffer Input = new();
            internal ManagedDragDrive Drive;
            internal ManagedDragLifetime Lifetime;
            internal float FirstOkAt, HeldAt, NextWaitLog, ArmCalm;
            internal bool LoggedFirstTarget, Remote, LoggedFallback;
            internal Player Dragger;
            internal string TargetSource = "input";
            internal readonly ManagedTargetSmoother Smoother = new();
            internal readonly ManagedHeightFilter HeightFilter = new();
            internal float HoldAboveGround = float.NaN;
            internal float VyMax, DyMin = float.PositiveInfinity, DyMax = float.NegativeInfinity;
            internal float Calm, LastTick, NextStatus;
            internal uint FrameSequence, StatusSequence;
            internal bool Ready, Ending, Failed;
            internal float EndedAt;
            internal Vector3 GripPoint;
            internal ulong LastStep;
            internal int Ticks, Steps, Inputs;
            internal float SimDtSum, MaxTickDt, LogAt;
        }
        // Resting Rupture limbs can jitter above the calm thresholds, so keep the cap short.
        private const float CalmWaitCap = 0.5f;
        // Limb assist waits for a looser calm than the grab gate, but never longer than ArmMaxWait.
        private const float ArmSpeed = 0.5f, ArmSpin = 3f, ArmCalmSeconds = 0.15f, ArmMaxWait = 1.5f;
        private static readonly Dictionary<string, Session> Sessions = new();
        private static readonly Dictionary<string, string> Profiles = new();
        private static GameWorld _world;
        internal static bool Begin(ManagedDragStart start)
        {
            if (!Plugin.Enabled.Value || !ManagedIntentBuffer.ValidSession(start.Session) || string.IsNullOrEmpty(start.ProfileId) ||
                (BodyDragSync.Active && !BodyDragSync.IsHost) || Profiles.ContainsKey(start.ProfileId) || Sessions.ContainsKey(start.Session) ||
                !Singleton<GameWorld>.Instantiated) return false;
            Corpse corpse = FindCorpse(start.ProfileId);
            if (corpse?.Ragdoll == null || (!Plugin.AllowZombieOrBotCorpses.Value && corpse.IsZombieCorpse) ||
                RuptureDragProvider.Inspect(corpse, out ProviderInfo info) != CorpseRoute.Managed || !info.IsAuthority || info.DeathSequence != start.DeathSequence)
                return false;
            ManagedDragSettings settings = ManagedDragSettings.Capture();
            if (!settings.Valid) return false;
            ProviderResult result = RuptureDragProvider.Begin(corpse, settings.Slack, settings.HandSpeed, out ulong lease);
            if ((result != ProviderResult.Pending && result != ProviderResult.Ok) || lease == 0) return false;
            float now = Time.realtimeSinceStartup;
            Session session = new Session { Identity = start, Corpse = corpse, Ragdoll = corpse.Ragdoll, Lease = lease,
                Settings = settings, Lifetime = new ManagedDragLifetime(now), LastTick = now };
            session.Dragger = string.IsNullOrEmpty(start.DraggerProfileId) ? null : Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(start.DraggerProfileId);
            session.Remote = BodyDragSync.Active && !(session.Dragger != null && session.Dragger.IsYourPlayer);
            _world = Singleton<GameWorld>.Instance;
            Sessions.Add(start.Session, session); Profiles.Add(start.ProfileId, start.Session);
            BodyDragLog.Info($"[Rupture] timing {start.ProfileId}: lease granted ({result}) t=0");
            Publish(session, ManagedDragStage.Preparing);
            return true;
        }
        internal static bool Input(ManagedDragInput input)
        {
            if (!Sessions.TryGetValue(input.Session ?? "", out Session session) || !ManagedIntentBuffer.Same(session.Identity.Session,
                session.Identity.ProfileId, session.Identity.DeathSequence, input.Session, input.ProfileId, input.DeathSequence) || session.Ending || !session.Input.Accept(input)) return false;
            session.Lifetime.Renew(Time.realtimeSinceStartup);
            session.Inputs++;
            return true;
        }
        internal static void End(ManagedDragEnd end)
        {
            if (Sessions.TryGetValue(end.Session ?? "", out Session session) && ManagedIntentBuffer.Same(session.Identity.Session,
                session.Identity.ProfileId, session.Identity.DeathSequence, end.Session, end.ProfileId, end.DeathSequence)) Close(session, false);
        }
        internal static void Tick()
        {
            if (Sessions.Count == 0) return;
            if (!Singleton<GameWorld>.Instantiated || !ReferenceEquals(_world, Singleton<GameWorld>.Instance) ||
                (BodyDragSync.Active && !BodyDragSync.IsHost)) { ReleaseAll(); return; }
            float now = Time.realtimeSinceStartup;
            foreach (Session session in new List<Session>(Sessions.Values))
            {
                try
                {
                    if (session.Ending)
                    {
                        ProviderResult endResult = RuptureDragProvider.Read(session.Lease, out _);
                        if (CanFinishEnd(endResult, now, session.EndedAt)) FinishClose(session, endResult != ProviderResult.Closed);
                        else if (now >= session.NextStatus) Publish(session, ManagedDragStage.Releasing);
                        continue;
                    }
                    if (!Plugin.Enabled.Value || session.Corpse == null || !ReferenceEquals(session.Corpse.Ragdoll, session.Ragdoll) ||
                        session.Lifetime.Expired(now, session.Ready)) { Close(session, false); continue; }
                    ProviderResult result = RuptureDragProvider.Read(session.Lease, out ProviderView view);
                    if (result == ProviderResult.Pending)
                    {
                        if (now >= session.NextStatus) Publish(session, ManagedDragStage.Preparing);
                        continue;
                    }
                    if (result != ProviderResult.Ok || view == null || view.GripIndex < 0 || view.GripIndex >= view.Bodies.Length ||
                        !view.Bodies[view.GripIndex].Eligible) { Close(session, result != ProviderResult.Closed); continue; }
                    float dt = Math.Max(0, now - session.LastTick);
                    session.LastTick = now;
                    session.GripPoint = view.Bodies[view.GripIndex].CenterOfMass;
                    if (Plugin.DebugLogging.Value) LogCadence(session, view, dt, now);
                    if (session.FirstOkAt == 0)
                    {
                        session.FirstOkAt = now;
                        BodyDragLog.Info($"[Rupture] timing {session.Identity.ProfileId}: lease ready (no longer Pending) t={(now - session.Lifetime.Started) * 1000f:F0}ms");
                    }
                    if (!session.Ready)
                    {
                        float maxSpeed2 = 0, maxSpin2 = 0;
                        foreach (ProviderBody body in view.Bodies)
                        {
                            if (!body.Eligible) continue;
                            maxSpeed2 = Math.Max(maxSpeed2, body.Velocity.sqrMagnitude);
                            maxSpin2 = Math.Max(maxSpin2, body.AngularVelocity.sqrMagnitude);
                        }
                        bool calm = maxSpeed2 <= .09f && maxSpin2 <= 1f;
                        session.Calm = calm ? session.Calm + dt : 0;
                        bool capped = now - session.Lifetime.Started >= CalmWaitCap;
                        if (session.Calm >= .3f || capped)
                        {
                            session.Ready = true;
                            session.HeldAt = now;
                            session.Drive = new ManagedDragDrive(session.Settings, view);
                            FindAxisBodies(session.Ragdoll, out session.Drive.HeadIndex, out session.Drive.PelvisIndex);
                            session.Drive.Suspend();
                            Publish(session, ManagedDragStage.Held);
                            BodyDragLog.Info($"[Rupture] timing {session.Identity.ProfileId}: Held published t={(now - session.Lifetime.Started) * 1000f:F0}ms " +
                                $"reason={(session.Calm >= .3f ? "calm" : "cap")} maxSpeed={Math.Sqrt(maxSpeed2):F2}m/s(limit .3) maxSpin={Math.Sqrt(maxSpin2):F2}rad/s(limit 1)");
                        }
                        else if (now >= session.NextWaitLog)
                        {
                            session.NextWaitLog = now + .5f;
                            BodyDragLog.Info($"[Rupture] timing {session.Identity.ProfileId}: waiting calm t={(now - session.Lifetime.Started) * 1000f:F0}ms " +
                                $"maxSpeed={Math.Sqrt(maxSpeed2):F2}m/s maxSpin={Math.Sqrt(maxSpin2):F2}rad/s calmAcc={session.Calm * 1000f:F0}ms");
                        }
                    }
                    else if (!session.LoggedFirstTarget && session.Input.HasTarget)
                    {
                        session.LoggedFirstTarget = true;
                        BodyDragLog.Info($"[Rupture] timing {session.Identity.ProfileId}: first client target applied {(now - session.HeldAt) * 1000f:F0}ms after Held");
                    }
                    if (session.Ready && !session.Drive.Armed) ArmLimbAssist(session, view, dt, now);
                    bool driving = session.Ready && session.Input.HasTarget;
                    Vector3 target = view.GripPosition;
                    float yaw = float.NaN;
                    if (driving)
                    {
                        target = ResolveTarget(session, out yaw);
                        if (session.Remote)
                            target = session.Smoother.Step(target, view.GripPosition, session.Settings.TargetSmoothing, dt,
                                session.Settings.HandSpeed, session.Settings.TeleportDistance);
                        target = HoldHeight(session, target, view, dt);
                        ProviderBody grip = view.Bodies[view.GripIndex];
                        session.VyMax = Math.Max(session.VyMax, Math.Abs(grip.Velocity.y));
                        session.DyMin = Math.Min(session.DyMin, target.y - grip.CenterOfMass.y);
                        session.DyMax = Math.Max(session.DyMax, target.y - grip.CenterOfMass.y);
                    }
                    else { session.Smoother.Reset(); session.HeightFilter.Reset(); }
                    ProviderAcceleration[] forces = driving ? session.Drive.Compose(view, target, dt, yaw) : Array.Empty<ProviderAcceleration>();
                    if (session.FrameSequence == uint.MaxValue) { Close(session, true); continue; }
                    result = RuptureDragProvider.Submit(session.Lease, ++session.FrameSequence, view.Topology, target, forces,
                        session.Drive?.TranslationId ?? 0, session.Drive?.Translation ?? Vector3.zero);
                    if (result != ProviderResult.Ok && result != ProviderResult.Stale) { Close(session, true); continue; }
                    if (now >= session.NextStatus) Publish(session, session.Ready ? ManagedDragStage.Held : ManagedDragStage.Preparing);
                }
                catch (Exception exception)
                {
                    BodyDragLog.Warning("[Rupture] Authority drag failed: " + exception.GetBaseException().Message);
                    Close(session, true);
                }
            }
        }
        private const float GroundHoldSmoothing = 0.15f;
        private static bool TryGroundY(Vector3 chest, float x, float z, out float y)
        {
            // Start just above the chest so a low ceiling above it is never the surface we find.
            Vector3 origin = new Vector3(x, chest.y + 0.5f, z);
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 3f, LayersMaskController.HighPolyWithTerrainMask))
            {
                y = hit.point.y;
                return true;
            }
            y = 0f;
            return false;
        }
        // Hold height = grab-time height above the terrain under the hold point, low-passed, rather
        // than the camera's height (bob/stance/slope would lift the chest off the floor and the
        // body hops). Falls back to the camera-derived height when no ground is found.
        private static Vector3 HoldHeight(Session session, Vector3 target, ProviderView view, float dt)
        {
            if (!Plugin.GroundFollowHold.Value) return target;
            Vector3 chest = view.Bodies[view.GripIndex].CenterOfMass;
            if (float.IsNaN(session.HoldAboveGround))
                session.HoldAboveGround = TryGroundY(chest, chest.x, chest.z, out float gripGround) ? Mathf.Clamp(chest.y - gripGround, 0.05f, 0.6f) : -1f;
            if (session.HoldAboveGround < 0f || !TryGroundY(chest, target.x, target.z, out float ground)) return target;
            target.y = session.HeightFilter.Step(ground + session.HoldAboveGround, chest.y, GroundHoldSmoothing, dt);
            return target;
        }
        // Rupture's body index is the original _rigidbodySpawners index; identity comes from the
        // spawner's BodyPartCollider, which outlives the native rigidbody.
        private static void FindAxisBodies(CorpseRagdoll ragdoll, out int head, out int pelvis)
        {
            head = pelvis = -1;
            RigidbodySpawner[] spawners = ragdoll?._rigidbodySpawners;
            if (spawners == null) return;
            for (int i = 0; i < spawners.Length; i++)
            {
                if (spawners[i] == null || !spawners[i].TryGetComponent(out BodyPartCollider part)) continue;
                if (part.BodyPartColliderType == EBodyPartColliderType.HeadCommon) head = i;
                else if (part.BodyPartColliderType == EBodyPartColliderType.Pelvis) pelvis = i;
            }
        }
        private static void ArmLimbAssist(Session session, ProviderView view, float dt, float now)
        {
            float speed2 = 0, spin2 = 0;
            foreach (ProviderBody body in view.Bodies)
            {
                if (!body.Eligible) continue;
                speed2 = Math.Max(speed2, body.Velocity.sqrMagnitude);
                spin2 = Math.Max(spin2, body.AngularVelocity.sqrMagnitude);
            }
            session.ArmCalm = speed2 <= ArmSpeed * ArmSpeed && spin2 <= ArmSpin * ArmSpin ? session.ArmCalm + dt : 0;
            bool calm = session.ArmCalm >= ArmCalmSeconds;
            if (!calm && now - session.HeldAt < ArmMaxWait) return;
            session.Drive.Arm(view);
            BodyDragLog.Info($"[Rupture] limb assist armed {(now - session.HeldAt) * 1000f:F0}ms after Held reason={(calm ? "calm" : "max-wait")} " +
                $"maxSpeed={Math.Sqrt(speed2):F2}m/s maxSpin={Math.Sqrt(spin2):F2}rad/s headLeads={session.Settings.HeadLeads} " +
                $"headIdx={session.Drive.HeadIndex} pelvisIdx={session.Drive.PelvisIndex} heading={session.Drive.HasHeading}");
        }
        // A remote dragger's own interpolated pose is continuous per frame; the 15Hz camera target
        // is only the fallback/sanity reference (and the whole path for local drags).
        private static Vector3 ResolveTarget(Session session, out float yaw)
        {
            ManagedDragInput input = session.Input.Latest;
            yaw = input.Yaw;
            session.TargetSource = "input";
            Player dragger = session.Dragger;
            if (!session.Remote || dragger == null || !dragger.isActiveAndEnabled || dragger.MovementContext == null)
                return session.Input.Target;
            Vector3 root = dragger.Position;
            float playerYaw = dragger.Yaw;
            Vector3 derived = root + Quaternion.Euler(0, playerYaw, 0) * new Vector3(input.LocalX, 0, input.LocalZ + input.Distance);
            derived.y = root.y + input.HeightOffset;
            if (!ManagedIntentBuffer.Finite(derived) || (derived - session.Input.Target).sqrMagnitude > session.Settings.HoldError * session.Settings.HoldError)
            {
                if (!session.LoggedFallback)
                {
                    session.LoggedFallback = true;
                    BodyDragLog.Info($"[Rupture] dragger pose target diverged from client target by {(derived - session.Input.Target).magnitude:F2}m; using client input");
                }
                return session.Input.Target;
            }
            yaw = playerYaw;
            session.TargetSource = "player";
            return derived;
        }
        private static void LogCadence(Session session, ProviderView view, float dt, float now)
        {
            session.Ticks++;
            session.MaxTickDt = Math.Max(session.MaxTickDt, dt);
            if (view.CompletedStep != session.LastStep)
            {
                session.LastStep = view.CompletedStep;
                session.Steps++;
                session.SimDtSum += view.SimulationDeltaTime;
            }
            if (session.LogAt == 0) session.LogAt = now + 2f;
            if (now < session.LogAt) return;
            float window = 2f + (now - session.LogAt);
            BodyDragLog.Info($"[Rupture] cadence {session.Identity.ProfileId}: ticks/s={session.Ticks / window:F1} steps/s={session.Steps / window:F1} " +
                $"avgSimDt={(session.Steps > 0 ? session.SimDtSum / session.Steps : 0f) * 1000f:F1}ms maxTickDt={session.MaxTickDt * 1000f:F0}ms inputs/s={session.Inputs / window:F1} " +
                $"gripVyMax={session.VyMax:F2}m/s targetMinusGripY={(float.IsInfinity(session.DyMin) ? 0f : session.DyMin):F2}..{(float.IsInfinity(session.DyMax) ? 0f : session.DyMax):F2}m holdAboveGround={session.HoldAboveGround:F2} " +
                $"remote={session.Remote} targetSource={session.TargetSource} smoothing={session.Settings.TargetSmoothing:F2}s yawFollow={session.Settings.YawFollow:F2}");
            session.Ticks = session.Steps = session.Inputs = 0;
            session.VyMax = 0; session.DyMin = float.PositiveInfinity; session.DyMax = float.NegativeInfinity;
            session.SimDtSum = session.MaxTickDt = 0;
            session.LogAt = now + 2f;
        }
        private static void Publish(Session session, ManagedDragStage stage)
        {
            session.NextStatus = Time.realtimeSinceStartup + .1f;
            ManagedDragStatus status = new ManagedDragStatus { Session = session.Identity.Session, ProfileId = session.Identity.ProfileId,
                DeathSequence = session.Identity.DeathSequence, Sequence = ++session.StatusSequence, Stage = stage, GripPoint = session.GripPoint };
            if (BodyDragSync.Active) BodyDragSync.ManagedStatusSent?.Invoke(status);
            else BodyDragSync.ApplyManagedStatus?.Invoke(status);
        }
        private static void Close(Session session, bool failed)
        {
            session.Failed |= failed;
            if (session.Ending) return;
            session.Ending = true;
            session.EndedAt = Time.realtimeSinceStartup;
            RuptureDragProvider.End(session.Lease);
            Publish(session, ManagedDragStage.Releasing);
        }
        internal static bool CanFinishEnd(ProviderResult result, float now, float endedAt) =>
            (result != ProviderResult.Ok && result != ProviderResult.Pending) || now - endedAt >= 6f;
        private static void FinishClose(Session session, bool failed)
        {
            if (!Sessions.Remove(session.Identity.Session)) return;
            Profiles.Remove(session.Identity.ProfileId);
            Publish(session, session.Failed || failed ? ManagedDragStage.Failed : ManagedDragStage.Closed);
        }
        internal static void ReleaseAll()
        {
            foreach (Session session in new List<Session>(Sessions.Values))
            {
                try { Close(session, false); }
                finally { FinishClose(session, false); }
            }
            _world = null;
        }
        internal static bool CanUseNative(string profileId)
        {
            Corpse corpse = FindCorpse(profileId);
            return corpse?.Ragdoll != null && RuptureDragProvider.Inspect(corpse, out _) == CorpseRoute.Native;
        }
        internal static Corpse FindCorpse(string profileId)
        {
            if (string.IsNullOrEmpty(profileId) || !Singleton<GameWorld>.Instantiated) return null;
            foreach (ObservedCorpse corpse in Singleton<GameWorld>.Instance.ObservedPlayersCorpses.Values)
                if (corpse != null && corpse.PlayerProfileID == profileId) return corpse;
            foreach (Corpse corpse in UnityEngine.Object.FindObjectsOfType<Corpse>())
                if (corpse != null && corpse.PlayerProfileID == profileId) return corpse;
            return null;
        }
    }
}
