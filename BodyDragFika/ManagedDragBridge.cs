using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BodyDragging;
using BodyDragging.Integration;
using Comfort.Common;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;

namespace BodyDragFika
{
    public static partial class BodyDragFikaBridge
    {
        private sealed class ManagedClaim
        {
            internal ManagedDragStart Identity;
            internal NetPeer Holder;
            internal ManagedDragStatus LastStatus;
        }
        private readonly struct MainThreadWork
        {
            internal readonly Action Action;
            internal readonly int Epoch;
            internal MainThreadWork(Action action, int epoch) { Action = action; Epoch = epoch; }
        }
        private static readonly ConcurrentQueue<MainThreadWork> MainThreadQueue = new();
        private static int _networkEpoch;
        private static readonly Dictionary<string, ManagedClaim> ManagedClaims = new();
        private static readonly Dictionary<string, ManagedDragStatus> KnownManagedClaims = new();
        private static void Enqueue(Action action, int epoch = -1) => MainThreadQueue.Enqueue(new MainThreadWork(action, epoch));
        private static void DrainMainThread()
        {
            for (int i = 0; i < 512 && MainThreadQueue.TryDequeue(out MainThreadWork work); i++)
            {
                if (work.Epoch != -1 && work.Epoch != _networkEpoch) continue;
                try { work.Action(); }
                catch (Exception exception) { _log?.LogError("[BodyDragFika] main-thread command failed: " + exception); }
            }
        }
        private static void AttachManagedTransport()
        {
            BodyDragSync.ManagedStartRequested = OnManagedStart;
            BodyDragSync.ManagedInputSent = OnManagedInput;
            BodyDragSync.ManagedEndRequested = OnManagedEnd;
            BodyDragSync.ManagedStatusSent = OnManagedStatusSent;
        }
        private static void DetachManagedTransport()
        {
            BodyDragSync.ManagedStartRequested = ManagedDragAuthority.Begin;
            BodyDragSync.ManagedInputSent = input => ManagedDragAuthority.Input(input);
            BodyDragSync.ManagedEndRequested = ManagedDragAuthority.End;
            BodyDragSync.ManagedStatusSent = null;
        }
        private static void ClearManagedClaims()
        {
            ManagedClaims.Clear(); KnownManagedClaims.Clear();
            _networkEpoch++;
        }
        private static bool OwnsManaged(string session, string profile, uint death, NetPeer peer) =>
            profile != null && ManagedClaims.TryGetValue(profile, out ManagedClaim claim) && ReferenceEquals(claim.Holder, peer) &&
            ManagedIntentBuffer.Same(claim.Identity.Session, claim.Identity.ProfileId, claim.Identity.DeathSequence, session, profile, death);

        private static void RegisterManagedServer(FikaServer server, int epoch)
        {
            server.RegisterPacket<ManagedDragStartPacket, NetPeer>((packet, peer) => Enqueue(() => StartManaged(packet.Value, peer), epoch));
            server.RegisterPacket<ManagedDragInputPacket, NetPeer>((packet, peer) => Enqueue(() =>
            {
                ManagedDragInput input = packet.Value;
                if (OwnsManaged(input.Session, input.ProfileId, input.DeathSequence, peer)) ManagedDragAuthority.Input(input);
            }, epoch));
            server.RegisterPacket<ManagedDragEndPacket, NetPeer>((packet, peer) => Enqueue(() =>
            {
                ManagedDragEnd end = packet.Value;
                if (OwnsManaged(end.Session, end.ProfileId, end.DeathSequence, peer)) ManagedDragAuthority.End(end);
            }, epoch));
        }
        private static void RegisterManagedClient(FikaClient client, int epoch) =>
            client.RegisterPacket<ManagedDragStatusPacket>(packet => Enqueue(() => ApplyManagedStatus(packet.Value), epoch));

        private static bool OnManagedStart(ManagedDragStart start)
        {
            if (!BodyDragSync.Active) return ManagedDragAuthority.Begin(start);
            if (!_registered) return false;
            if (BodyDragSync.IsHost) return StartManaged(start, null);
            FikaClient client = Singleton<FikaClient>.Instance;
            if (client == null) return false;
            ManagedDragStartPacket packet = new ManagedDragStartPacket { Value = start };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            return true;
        }
        private static bool StartManaged(ManagedDragStart start, NetPeer peer)
        {
            if (!BodyDragSync.Active || !BodyDragSync.IsHost || !ManagedIntentBuffer.ValidSession(start.Session) || string.IsNullOrEmpty(start.ProfileId)) return false;
            if (OwnsManaged(start.Session, start.ProfileId, start.DeathSequence, peer))
            {
                SendStatusTo(ManagedClaims[start.ProfileId].LastStatus, peer);
                return true;
            }
            if (Claims.ContainsKey(start.ProfileId) || ManagedClaims.ContainsKey(start.ProfileId)) { DenyManaged(start, peer); return false; }
            ManagedClaims.Add(start.ProfileId, new ManagedClaim { Identity = start, Holder = peer });
            Claims[start.ProfileId] = peer;
            if (ManagedDragAuthority.Begin(start)) return true;
            ManagedClaims.Remove(start.ProfileId); Claims.Remove(start.ProfileId);
            DenyManaged(start, peer);
            return false;
        }
        private static void DenyManaged(ManagedDragStart start, NetPeer peer) => SendStatusTo(new ManagedDragStatus {
            Session = start.Session, ProfileId = start.ProfileId, DeathSequence = start.DeathSequence,
            Sequence = 1, Stage = ManagedDragStage.Failed }, peer);
        private static void SendStatusTo(ManagedDragStatus status, NetPeer peer)
        {
            if (peer == null) BodyDragSync.ApplyManagedStatus?.Invoke(status);
            else
            {
                ManagedDragStatusPacket packet = new ManagedDragStatusPacket { Value = status };
                Singleton<FikaServer>.Instance?.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
            }
        }
        private static void OnManagedInput(ManagedDragInput input)
        {
            if (!BodyDragSync.Active) { ManagedDragAuthority.Input(input); return; }
            if (BodyDragSync.IsHost)
            {
                if (OwnsManaged(input.Session, input.ProfileId, input.DeathSequence, null)) ManagedDragAuthority.Input(input);
            }
            else if (_registered)
            {
                ManagedDragInputPacket packet = new ManagedDragInputPacket { Value = input };
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.Sequenced);
            }
        }
        private static void OnManagedEnd(ManagedDragEnd end)
        {
            if (!BodyDragSync.Active) { ManagedDragAuthority.End(end); return; }
            if (BodyDragSync.IsHost)
            {
                if (OwnsManaged(end.Session, end.ProfileId, end.DeathSequence, null)) ManagedDragAuthority.End(end);
            }
            else if (_registered)
            {
                ManagedDragEndPacket packet = new ManagedDragEndPacket { Value = end };
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }
        private static void OnManagedStatusSent(ManagedDragStatus status)
        {
            if (!BodyDragSync.IsHost || !ManagedClaims.TryGetValue(status.ProfileId, out ManagedClaim claim) ||
                !ManagedIntentBuffer.Same(claim.Identity.Session, claim.Identity.ProfileId, claim.Identity.DeathSequence,
                    status.Session, status.ProfileId, status.DeathSequence)) return;
            claim.LastStatus = status;
            ApplyManagedStatus(status);
            ManagedDragStatusPacket packet = new ManagedDragStatusPacket { Value = status };
            try { Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered); }
            catch (Exception exception) { _log?.LogWarning("[BodyDragFika] managed status send failed: " + exception.Message); }
            finally
            {
                if (status.Stage == ManagedDragStage.Closed || status.Stage == ManagedDragStage.Failed)
                {
                    ManagedClaims.Remove(status.ProfileId); Claims.Remove(status.ProfileId);
                }
            }
        }
        private static void ApplyManagedStatus(ManagedDragStatus status)
        {
            bool terminal = status.Stage == ManagedDragStage.Closed || status.Stage == ManagedDragStage.Failed;
            if (KnownManagedClaims.TryGetValue(status.ProfileId, out ManagedDragStatus previous))
            {
                bool same = ManagedIntentBuffer.Same(previous.Session, previous.ProfileId, previous.DeathSequence,
                    status.Session, status.ProfileId, status.DeathSequence);
                if (same && status.Sequence <= previous.Sequence) return;
                if (terminal && !same) { BodyDragSync.ApplyManagedStatus?.Invoke(status); return; }
            }
            if (terminal)
            {
                KnownManagedClaims.Remove(status.ProfileId); RemotelyClaimedProfiles.Remove(status.ProfileId);
            }
            else
            {
                KnownManagedClaims[status.ProfileId] = status; RemotelyClaimedProfiles.Add(status.ProfileId);
            }
            BodyDragSync.ApplyManagedStatus?.Invoke(status);
        }
    }
}
