using BodyDragging;
using Comfort.Common;
using EFT;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace BodyDragFika
{
    // The dragger's raised-left-arm pose (DragHands) has no Fika sync of its own: tell the other
    // peers who is armed-dragging so they raise the arm on that player's observed copy too.
    public struct DragHandPacket : INetSerializable
    {
        public string Player;
        public bool On;
        // profile id of the corpse / downed player being dragged: the observer's wrist IK target
        public string Body;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Player ?? ""); writer.Put(On); writer.Put(Body ?? "");
        }
        public void Deserialize(NetDataReader reader)
        {
            Player = reader.GetString(128); On = reader.GetBool(); Body = reader.GetString(128);
        }
    }

    public static partial class BodyDragFikaBridge
    {
        private static void RegisterHandsServer(FikaServer server, int epoch) =>
            server.RegisterPacket<DragHandPacket, NetPeer>((packet, peer) => Enqueue(() =>
            {
                BodyDragSync.ApplyRemoteHandPose?.Invoke(packet.Player, packet.On, packet.Body);
                server.SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
            }, epoch));

        private static void RegisterHandsClient(FikaClient client, int epoch) =>
            client.RegisterPacket<DragHandPacket>(packet => Enqueue(() =>
                BodyDragSync.ApplyRemoteHandPose?.Invoke(packet.Player, packet.On, packet.Body), epoch));

        private static void OnLocalHandPose(bool on, string body)
        {
            Player me = GamePlayerOwner.MyPlayer;
            if (!BodyDragSync.Active || me == null)
                return;
            DragHandPacket packet = new DragHandPacket { Player = me.ProfileId, On = on, Body = body };
            if (BodyDragSync.IsHost)
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            else
                Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
    }
}
