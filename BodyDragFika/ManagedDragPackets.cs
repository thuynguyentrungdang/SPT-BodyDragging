using System;
using BodyDragging.Integration;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace BodyDragFika
{
    internal static class ManagedDragWire
    {
        internal static void Write(NetDataWriter writer, string session, string profile, uint death)
        {
            writer.Put((byte)1); writer.Put(session); writer.Put(profile); writer.Put(death);
        }
        internal static void Read(NetDataReader reader, out string session, out string profile, out uint death)
        {
            if (reader.GetByte() != 1) throw new FormatException("unsupported managed drag protocol");
            session = reader.GetString(32); profile = reader.GetString(128); death = reader.GetUInt();
            if (!ManagedIntentBuffer.ValidSession(session) || string.IsNullOrEmpty(profile)) throw new FormatException("invalid managed drag identity");
        }
    }
    public struct ManagedDragStartPacket : INetSerializable
    {
        public ManagedDragStart Value;
        public void Serialize(NetDataWriter writer) => ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
        public void Deserialize(NetDataReader reader)
        {
            ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
        }
    }
    public struct ManagedDragInputPacket : INetSerializable
    {
        public ManagedDragInput Value;
        public void Serialize(NetDataWriter writer)
        {
            ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
            writer.Put(Value.Sequence); writer.Put(Value.HasTarget); writer.PutUnmanaged(Value.Target);
        }
        public void Deserialize(NetDataReader reader)
        {
            ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
            Value.Sequence = reader.GetUInt(); Value.HasTarget = reader.GetBool(); Value.Target = reader.GetUnmanaged<Vector3>();
            if (Value.Sequence == 0 || !ManagedIntentBuffer.Finite(Value.Target)) throw new FormatException("invalid managed drag target");
        }
    }
    public struct ManagedDragEndPacket : INetSerializable
    {
        public ManagedDragEnd Value;
        public void Serialize(NetDataWriter writer) => ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
        public void Deserialize(NetDataReader reader) => ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
    }
    public struct ManagedDragStatusPacket : INetSerializable
    {
        public ManagedDragStatus Value;
        public void Serialize(NetDataWriter writer)
        {
            ManagedDragWire.Write(writer, Value.Session, Value.ProfileId, Value.DeathSequence);
            writer.Put(Value.Sequence); writer.Put((byte)Value.Stage); writer.PutUnmanaged(Value.GripPoint);
        }
        public void Deserialize(NetDataReader reader)
        {
            ManagedDragWire.Read(reader, out Value.Session, out Value.ProfileId, out Value.DeathSequence);
            Value.Sequence = reader.GetUInt(); Value.Stage = (ManagedDragStage)reader.GetByte(); Value.GripPoint = reader.GetUnmanaged<Vector3>();
            if (Value.Sequence == 0 || Value.Stage > ManagedDragStage.Failed || !ManagedIntentBuffer.Finite(Value.GripPoint)) throw new FormatException("invalid managed drag status");
        }
    }
}
