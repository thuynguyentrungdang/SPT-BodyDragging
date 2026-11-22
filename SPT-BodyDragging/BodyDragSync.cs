using System;
using UnityEngine;
using BodyDragging.Integration;

namespace BodyDragging
{
    // Seam between the main plugin and the optional BodyDragFika bridge DLL. No Fika type is
    // named here - the bridge is loaded by reflection only when Fika.Core is present in the
    // chainloader (see Plugin.TryLoadFikaBridge), so solo play never touches this beyond the
    // Active check staying false.
    public static class BodyDragSync
    {
        public static bool Active;
        public static bool IsHost;
        public static bool HeadlessHost;

        // the bridge's packet handlers can only attach once Fika's network manager exists;
        // the main plugin already runs an Update loop, so it drives the bridge's retry from there
        public static Action Tick;

        // set by the Fika bridge while the local player drags a downed teammate, so the same
        // movement slowdown as the corpse drag applies
        internal static bool ExternalDragActive;

        public static Func<ManagedDragStart, bool> ManagedStartRequested = ManagedDragAuthority.Begin;
        public static Action<ManagedDragInput> ManagedInputSent = input => ManagedDragAuthority.Input(input);
        public static Action<ManagedDragEnd> ManagedEndRequested = ManagedDragAuthority.End;
        public static Action<ManagedDragStatus> ManagedStatusSent;
        public static Action<ManagedDragStatus> ApplyManagedStatus;

        // Native drag: no pose stream. Every peer simulates its own copy of the body, held by the
        // dragger's synced position plus the hold parameters below; the peer that lets go sends one
        // rest (the settled pose) so all copies end up the same.
        // outgoing, main plugin -> bridge
        public static Action<string> DragStartRequested;
        public static Action<BodyDragHold> HoldSent;
        public static Action<string> DragStoppedLocally;
        public static Action<BodyDragRest> RestDeclared;

        // incoming, bridge -> main plugin
        public static Action<string> ApplyDragDenied;
        public static Action<string, string> ApplyRemoteStart;   // corpse profile id, dragger profile id
        public static Action<BodyDragHold> ApplyRemoteHold;
        public static Action<string> ApplyRemoteStop;
        public static Action<BodyDragRest> ApplyRemoteRest;

        // left-arm "one-handed weapon" pose of a dragger (see DragHands): local state out, remote in
        // args: pose on, profile id of the body being dragged (grip target for the wrist IK)
        public static Action<bool, string> HandPoseSent;
        public static Action<string, bool, string> ApplyRemoteHandPose;

        // bridge-provided lookups for a Fika downed player's local ragdoll (no Fika types here)
        public static Func<string, Rigidbody> ResolveDownedChest;
        public static Func<Collider, bool> IsDraggedDownedCollider;

        // bridge asks before granting a claim locally observed as free
        public static Func<string, bool> IsProfileAlreadyDragged;

        public static void LeaveRaid()
        {
            Active = false;
            IsHost = false;
            HeadlessHost = false;
        }
    }

    // The dragger's hold, in the dragger's yaw frame: target = root + R(Yaw) * (LocalX, 0, LocalZ + Distance),
    // height = root.y + HeightOffset (see RemoteHoldMath).
    public struct BodyDragHold
    {
        public string ProfileId;
        public uint Sequence;
        public float Yaw;
        public float Distance;
        public float LocalX;
        public float LocalZ;
        public float HeightOffset;
    }

    // Where a let-go body came to rest on the peer that dropped it: corpse root and every ragdoll bone.
    public struct BodyDragRest
    {
        public string ProfileId;
        public Vector3 Root;
        public Vector3[] BonePositions;
        public Quaternion[] BoneRotations;
    }
}
