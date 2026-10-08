using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using BodyDragging;
using BodyDragging.Integration;
using BodyDragFika;
using EFT.Interactive;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using Mono.Cecil;
using Rupture.Integration;
using UnityEngine;

internal static class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int _checks;
    private static readonly string Session = new string('a', 32);
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        _checks++; Console.WriteLine("PASS: " + name);
    }
    private static void Set(Type type, string name, object value) => type.GetField(name, All).SetValue(null, value);
    private static object Call(Type type, string name, params object[] args) => type.GetMethod(name, All).Invoke(null, args);
    private static void Main(string[] args)
    {
        TestIntentAndLifetime(); TestAbiAdapter(); TestDrive(); TestWire(); TestClaimsAndQueue(); TestArchitecture(); TestDowned();
        if (args.Length == 2) NativeDriveTests.Run(args[0],args[1],Check);
        Console.WriteLine($"ALL PASS: {_checks} checks; live EFT/Fika tests are not represented by these fixtures.");
    }
    private static void TestDowned()
    {
        Vector3 dragger = new(0,0,0);
        Check(DownedDragMath.TrailPoint(dragger,new Vector3(0,.2f,1)) == new Vector3(0,.2f,1), "body inside the trail distance is not pushed");
        Vector3 held = DownedDragMath.TrailPoint(dragger,new Vector3(0,.2f,5));
        Check(Math.Abs(held.z-DownedDragMath.TrailDistance) < 1e-4f && held.y == .2f, "body beyond the trail distance is pulled to a point 1.2m behind the dragger");
        Check(DownedDragMath.FollowMotion(new Vector3(0,0,.02f),Vector3.zero,.016f) == Vector3.zero, "follow dead zone ignores sub-3cm gaps");
        Vector3 far = DownedDragMath.FollowMotion(new Vector3(0,0,100),Vector3.zero,.016f);
        Check(Math.Abs(far.magnitude-DownedDragMath.MaxCatchUpSpeed*.016f) < 1e-4f && far.y == 0, "catch-up speed is capped, horizontal only");
        Vector3 near = DownedDragMath.FollowMotion(new Vector3(0,0,1),Vector3.zero,.016f);
        Check(near.z > 0 && near.z < DownedDragMath.FollowGain*.016f*1f + 1e-4f, "catch-up is proportional to the gap");
        Check(Math.Abs(DownedDragMath.RemoteHold(Vector3.zero,new Vector3(0,0,3)).z-DownedDragMath.RemotePullOffset) < 1e-4f &&
            DownedDragMath.RemoteHold(Vector3.zero,new Vector3(0,0,.2f)).z == .2f && DownedDragMath.RemoteHold(Vector3.zero,Vector3.zero) == Vector3.zero,
            "observer hold is min(0.5m, distance) from the root toward the dragger");
        var packet = Roundtrip(new DownedDragPacket { Op=DownedDragPacket.Start, Downed="downed", Dragger="dragger" });
        Check(packet.Op == DownedDragPacket.Start && packet.Downed == "downed" && packet.Dragger == "dragger", "downed-drag claim roundtrip");
    }
    private static void TestIntentAndLifetime()
    {
        ManagedIntentBuffer buffer = new();
        ManagedDragInput input = new() { Session = Session, ProfileId = "corpse", DeathSequence = 3, Sequence = 1, HasTarget = true, Target = new Vector3(1,2,3) };
        Check(ManagedIntentBuffer.ValidSession(Session) && !ManagedIntentBuffer.ValidSession("old") && !ManagedIntentBuffer.ValidSession(new string('x',32)), "session identity is a full GUID");
        Check(buffer.Accept(input), "fresh input accepted");
        input.Target.x = 99;
        Check(buffer.Target.x == 1 && !buffer.Accept(input), "input is copied and duplicate cannot renew");
        input.Sequence = 2; input.Target.x = float.NaN;
        Check(!buffer.Accept(input) && buffer.Sequence == 1, "nonfinite input cannot consume sequence");
        input.Target.x = 2;
        Check(buffer.Accept(input) && buffer.Sequence == 2, "rejected input can be retried with valid data");
        input.Sequence = 1;
        Check(!buffer.Accept(input), "out-of-order intent rejected");
        Check(!ManagedIntentBuffer.Same(Session,"corpse",3,new string('b',32),"corpse",3) &&
            !ManagedIntentBuffer.Same(Session,"corpse",3,Session,"corpse",4), "session and death distinguish delayed commands");
        ManagedDragLifetime life = new(100);
        Check(!life.Expired(101.99f,true) && life.Expired(102,true), "lost input releases at two seconds");
        life.Renew(101.9f);
        Check(!life.Expired(103,true) && life.Expired(103.9f,true), "accepted input renews bounded hold");
        life.Renew(105.9f);
        Check(life.Expired(106,false) && !life.Expired(106,true), "preparation deadline stays bounded despite heartbeat");
        Check(!ManagedDragAuthority.CanFinishEnd(ProviderResult.Pending,101,100) && !ManagedDragAuthority.CanFinishEnd(ProviderResult.Ok,101,100) &&
            ManagedDragAuthority.CanFinishEnd(ProviderResult.Closed,101,100),"claim remains reserved until queued ABI release completes");
        Check(ManagedDragAuthority.CanFinishEnd(ProviderResult.InvalidLease,101,100) && ManagedDragAuthority.CanFinishEnd(ProviderResult.Pending,106,100),
            "invalid handles and stalled release have bounded cleanup");
    }
    private static void TestAbiAdapter()
    {
        Type provider = typeof(RuptureDragProvider);
        Set(provider,"_present",false);
        Check(RuptureDragProvider.Inspect(null,out _) == CorpseRoute.Native, "Rupture absent retains standalone route");
        Set(provider,"_present",true); Set(provider,"_broken",true);
        Check(RuptureDragProvider.Inspect(null,out _) == CorpseRoute.Blocked, "missing/broken ABI cannot authorize native takeover");
        Set(provider,"_broken",false);
        Type shape = provider.GetNestedType("Shape",All);
        foreach (var pair in new[] { ("_beginShape","DragBegin"), ("_frameShape","DragFrame"), ("_forceShape","DragAcceleration"),
            ("_viewShape","DragView"), ("_bodyShape","DragBodyView"), ("_infoShape","CorpseControlInfo") })
            Set(provider,pair.Item1,Activator.CreateInstance(shape,All,null,new object[] { typeof(CorpseDragV1).Assembly,pair.Item2 },null));
        foreach (string method in new[] { "Inspect","Begin","Read","Submit","End" }) Set(provider,"_"+method.ToLowerInvariant(),typeof(FixtureAbi).GetMethod(method));
        Set(provider,"_released",DragEndReason.Released);
        FixtureAbi.Result = DragResult.Ok;
        Check(RuptureDragProvider.Inspect(null,out ProviderInfo info) == CorpseRoute.Managed && info.IsAuthority && info.DeathSequence == 3, "adapter decodes actual V1 DTOs and capability flags");
        FixtureAbi.Result = DragResult.NotManaged;
        Check(RuptureDragProvider.Inspect(null,out _) == CorpseRoute.Native, "only explicit NotManaged allows native route");
        FixtureAbi.Result = DragResult.Unsupported;
        Check(RuptureDragProvider.Inspect(null,out _) == CorpseRoute.Blocked, "unavailable managed ownership is blocked");
        FixtureAbi.Result = DragResult.Pending;
        Check(RuptureDragProvider.Begin(null,.05f,4.5f,out ulong lease) == ProviderResult.Pending && lease == 42 &&
            FixtureAbi.BeginRequest.PreferredBody == null && FixtureAbi.BeginRequest.LeaseTimeout == 5, "acquisition passes exact V1 settings without a native body requirement");
        FixtureAbi.Result = DragResult.Ok;
        Check(RuptureDragProvider.Read(lease,out ProviderView view) == ProviderResult.Ok && view.CompletedStep == 90 && view.SimulationDeltaTime == .1f && view.Topology == 7 &&
            view.GripIndex == 0 && view.Bodies[1].Index == 1 && !view.Bodies[1].Eligible, "read maps body topology and completed states");
        FixtureAbi.Bodies[0].Position.x = 99;
        Check(view.Bodies[0].Position.x == 0, "read state cannot alias provider body array");
        object viewShape=provider.GetField("_viewShape",All).GetValue(null);
        IDictionary viewFields=(IDictionary)shape.GetField("_fields",All).GetValue(viewShape);
        object stepField=viewFields["SimulationDeltaTime"]; viewFields.Remove("SimulationDeltaTime");
        Check(RuptureDragProvider.Read(lease,out ProviderView olderView)==ProviderResult.Ok && olderView.SimulationDeltaTime==0,
            "older V1 provider uses cadence fallback when additive timing field is absent");
        viewFields.Add("SimulationDeltaTime",stepField);
        var forces = new[] { new ProviderAcceleration { Index=0, Value=new Vector3(0,8,0) } };
        Check(RuptureDragProvider.Submit(lease,1,7,new Vector3(1,2,3),forces,5,new Vector3(10,0,0)) == ProviderResult.Ok &&
            FixtureAbi.Frame.Sequence == 1 && FixtureAbi.Frame.TopologyRevision == 7 && FixtureAbi.Frame.Accelerations[0].Acceleration.y == 8 &&
            FixtureAbi.Frame.TranslationId == 5 && FixtureAbi.Frame.RigTranslation.x == 10, "submit constructs the actual ABI frame and recovery request");
        forces[0].Value.y = 99;
        Check(FixtureAbi.Frame.Accelerations[0].Acceleration.y == 8, "submit owns its acceleration array");
        Check(RuptureDragProvider.Submit(lease,2,7,Vector3.zero,Array.Empty<ProviderAcceleration>(),0,Vector3.zero) == ProviderResult.Ok &&
            FixtureAbi.Frame.Sequence == 2 && FixtureAbi.Frame.Accelerations.Length == 0 && FixtureAbi.Frame.TranslationId == 0, "empty assistance clears forces and refreshes every field");
        FixtureAbi.Bodies[0].Position.x = 0; FixtureAbi.Step = 90; FixtureAbi.Result = DragResult.Ok;
        RuptureDragProvider.End(lease);
        Check(FixtureAbi.EndLease == lease && FixtureAbi.EndReason == DragEndReason.Released, "release delegates to Rupture without final poses");
    }
    private static ProviderView View() => new() { GripIndex=0, Topology=1, Bodies=new[] {
        new ProviderBody { Index=0, Eligible=true, Position=Vector3.zero, CenterOfMass=Vector3.zero },
        new ProviderBody { Index=1, Eligible=true, Position=new Vector3(1,0,0), CenterOfMass=new Vector3(1,0,0) },
        new ProviderBody { Index=2, Eligible=false, Position=new Vector3(2,0,0) } } };
    private static void TestDrive()
    {
        ManagedDragSettings settings = new() { Slack=.05f, HandSpeed=4.5f, TeleportDistance=2.5f, HoldError=2, LimbSpring=4000, MaxAcceleration=8000 };
        Check(settings.Valid, "host physics settings valid");
        foreach (bool headless in new[] { false,true })
        {
            BodyDragSync.HeadlessHost = headless;
            ProviderView view = View(); ManagedDragDrive drive = new(settings,view);
            view.Bodies[0].Position.x = .1f; view.Bodies[0].CenterOfMass.x = .1f;
            ProviderAcceleration[] force = drive.Compose(view,new Vector3(.2f,0,0));
            float expected = 400f / (1f + 2.2f * (float)Math.Sqrt(4000) / 30f + 4000f / 900f);
            Check(force.Length == 1 && force[0].Index == 1 && Math.Abs(force[0].Value.x-expected) < .001 && drive.TranslationId == 0,
                "eligible limb assistance from solver state, role headless="+headless);
            drive.Compose(view,new Vector3(10,0,0));
            uint recovery = drive.TranslationId; Vector3 offset = drive.Translation;
            Check(recovery == 1 && offset.x > 9, "large target jump requests common relocation, role headless="+headless);
            drive.Compose(view,new Vector3(20,0,0));
            Check(drive.TranslationId == recovery && drive.Translation.x == offset.x, "coalescing cannot replace/replay pending relocation, role headless="+headless);
            view.TranslationId = recovery; view.Bodies[0].CenterOfMass.x = 20;
            drive.Compose(view,new Vector3(20,0,0));
            Check(drive.TranslationId == 0, "ack retires recovery exactly once, role headless="+headless);
            view.Bodies[1].Eligible = false;
            Check(drive.Compose(view,new Vector3(20,0,0)).Length == 0, "new severing excludes a formerly eligible limb, role headless="+headless);
        }
        ManagedDragSettings yawSettings = new() { Slack=.05f, HandSpeed=4.5f, TeleportDistance=2.5f, HoldError=2, LimbSpring=4000, MaxAcceleration=8000, YawFollow=1 };
        ProviderView yawView = View(); ManagedDragDrive yawDrive = new(yawSettings,yawView);
        ProviderAcceleration[] noSwing = yawDrive.Compose(yawView,Vector3.zero,0,0);
        ProviderAcceleration[] swung = yawDrive.Compose(yawView,Vector3.zero,0,90);
        Check(noSwing.Length == 1 && noSwing[0].Value.sqrMagnitude < 1e-6f && swung.Length == 1 && swung[0].Value.x < 0 && swung[0].Value.z < 0,
            "yaw follow swings limb target around the grip as the dragger turns");
        yawSettings.YawFollow = 0;
        ProviderView flatView = View(); ManagedDragDrive flatDrive = new(yawSettings,flatView);
        Check(flatDrive.Compose(flatView,Vector3.zero,0,0)[0].Value.sqrMagnitude < 1e-6f && flatDrive.Compose(flatView,Vector3.zero,0,90)[0].Value.sqrMagnitude < 1e-6f,
            "zero yaw follow keeps translation-only assistance");
        ManagedDragSettings headSettings = new() { Slack=.05f, HandSpeed=4.5f, TeleportDistance=2.5f, HoldError=2, LimbSpring=4000, MaxAcceleration=8000, HeadLeads=true, HeadLeadTurnRate=120 };
        ProviderView headView = View(); ManagedDragDrive headDrive = new(headSettings,headView) { HeadIndex=1 }; headDrive.Suspend(); headDrive.Arm(headView);
        Check(headDrive.HasHeading, "head axis found from chest to head body");
        ProviderAcceleration[] gentle = headDrive.Compose(headView,Vector3.zero,.1f,0);
        ManagedDragSettings quickSettings = new() { Slack=.05f, HandSpeed=4.5f, TeleportDistance=2.5f, HoldError=2, LimbSpring=4000, MaxAcceleration=8000, HeadLeads=true, HeadLeadTurnRate=360 };
        ProviderView headView2 = View(); ManagedDragDrive fast = new(quickSettings,headView2) { HeadIndex=1 }; fast.Suspend(); fast.Arm(headView2);
        ProviderAcceleration[] quick = fast.Compose(headView2,Vector3.zero,.1f,0);
        Check(gentle.Length==1 && gentle[0].Value.x < 0 && gentle[0].Value.z < 0 && quick[0].Value.z < gentle[0].Value.z,
            "head-leads turns the head end toward the dragger (opposite their facing), faster turn rate swings further per step");
        ManagedDragSettings flipSettings = new() { Slack=.05f, HandSpeed=4.5f, TeleportDistance=2.5f, HoldError=2, LimbSpring=4000, MaxAcceleration=8000, HeadLeads=true, HeadLeadTurnRate=360 };
        ProviderView headView3 = View(); ManagedDragDrive settled = new(flipSettings,headView3) { HeadIndex=1 }; settled.Suspend(); settled.Arm(headView3);
        for (int i=0;i<60;i++) settled.Compose(headView3,Vector3.zero,.1f,0);
        ProviderView facing = View(); facing.Bodies[1].Position = new Vector3(0,0,-1); facing.Bodies[1].CenterOfMass = facing.Bodies[1].Position;
        Check(settled.Compose(facing,Vector3.zero,.1f,0)[0].Value.sqrMagnitude < 1e-3f, "head already toward dragger needs no further swing once the turn completes");
        ProviderView noHeadView = View(); noHeadView.Bodies[1].Eligible = false;
        ManagedDragDrive noHead = new(headSettings,noHeadView) { HeadIndex=1 }; noHead.Suspend(); noHead.Arm(noHeadView);
        Check(!noHead.HasHeading, "missing head and pelvis disables head-leads orientation instead of guessing");
        ManagedHeightFilter heightFilter = new();
        float h1 = heightFilter.Step(1f,0f,.15f,.016f); float h2 = h1; for (int i=0;i<120;i++) h2 = heightFilter.Step(1f,0f,.15f,.016f);
        Check(h1 > 0 && h1 < .2f && Mathf.Abs(h2-1f) < .01f && h2 <= 1.001f, "hold height low-pass starts at the chest and converges without overshoot");
        heightFilter.Reset();
        Check(heightFilter.Step(5f,2f,.15f,.016f) < 2.5f, "reset restarts the height filter from the current chest height");
        ManagedTargetSmoother smoother = new();
        Vector3 start0 = new(0,0,0), far = new(1,0,0);
        Check(smoother.Step(far,start0,0,.016f,4.5f,2.5f) == far, "zero smoothing passes the raw target through");
        Vector3 s1 = smoother.Step(far,start0,.08f,.016f,4.5f,2.5f);
        Vector3 s2 = s1; for (int i=0;i<60;i++) s2 = smoother.Step(far,start0,.08f,.016f,4.5f,2.5f);
        Check(s1.x > 0 && s1.x < .5f && Mathf.Abs(s2.x-1) <= ManagedTargetSmoother.DeadZone + .005f, "smoothing converges on a staircase target to within the dead zone");
        ManagedTargetSmoother quiet = new(); Vector3 held = quiet.Step(Vector3.zero,Vector3.zero,.08f,.016f,4.5f,2.5f);
        for (int i=0;i<30;i++) held = quiet.Step(new Vector3(.01f*(i%2),.01f*((i+1)%2),0),Vector3.zero,.08f,.016f,4.5f,2.5f);
        Check(held == Vector3.zero, "sub-dead-zone target noise never moves the hand");
        ManagedDragSettings armSettings = new() { Slack=.05f, HandSpeed=4.5f, TeleportDistance=2.5f, HoldError=2, LimbSpring=4000, MaxAcceleration=8000 };
        ProviderView armView = View(); ManagedDragDrive armDrive = new(armSettings,armView); armDrive.Suspend();
        armView.Bodies[1].Position.x = 3;
        Check(!armDrive.Armed && armDrive.Compose(armView,Vector3.zero).Length == 0, "suspended limb assist applies no force while the corpse is still tumbling");
        armDrive.Arm(armView);
        Check(armDrive.Armed && armDrive.Compose(armView,Vector3.zero)[0].Value.sqrMagnitude < 1e-6f, "arming snapshots the current shape, so assist starts with zero error");
        Check(smoother.Step(new Vector3(10,0,0),start0,.08f,.016f,4.5f,2.5f).x > 9, "target jump beyond teleport distance snaps instead of dragging");
        settings.HandSpeed = float.NaN;
        Check(!settings.Valid, "nonfinite configuration cannot acquire physics");
        settings.HandSpeed=4.5f; settings.LimbSpring=0;
        ProviderView noAssist=View(); ManagedDragDrive freeLimbs=new(settings,noAssist); noAssist.Bodies[1].Velocity.x=30;
        Check(freeLimbs.Compose(noAssist,new Vector3(0,0,0)).Length==0,"zero limb-follow strength disables all assistance");
    }
    private static T Roundtrip<T>(T value) where T : struct, INetSerializable
    {
        NetDataWriter writer = new(); value.Serialize(writer); T copy = default; NetDataReader reader = new(writer.CopyData()); copy.Deserialize(reader);
        Check(reader.AvailableBytes == 0,"exact packet payload: "+typeof(T).Name); return copy;
    }
    private static void TestWire()
    {
        var start = Roundtrip(new ManagedDragStartPacket { Value=new ManagedDragStart { Session=Session,ProfileId="corpse",DeathSequence=3,DraggerProfileId="dragger" } });
        Check(start.Value.Session == Session && start.Value.DeathSequence == 3 && start.Value.DraggerProfileId == "dragger","begin identity and dragger roundtrip");
        var input = Roundtrip(new ManagedDragInputPacket { Value=new ManagedDragInput { Session=Session,ProfileId="corpse",DeathSequence=3,
            Sequence=12,HasTarget=true,Target=new Vector3(1,2,3),Distance=.33f,LocalX=.1f,LocalZ=.4f,HeightOffset=-.5f,Yaw=270 } });
        Check(input.Value.Sequence == 12 && input.Value.HasTarget && input.Value.Target.z == 3 && input.Value.Distance == .33f &&
            input.Value.LocalZ == .4f && input.Value.HeightOffset == -.5f && input.Value.Yaw == 270,"target and camera-relative hold intent roundtrip");
        ManagedIntentBuffer holdBuffer = new();
        Check(!holdBuffer.Accept(new ManagedDragInput { Sequence=1,Yaw=float.NaN }) && holdBuffer.Accept(input.Value) && holdBuffer.Latest.Yaw == 270,
            "nonfinite hold fields cannot be accepted, latest hold retained");
        var end = Roundtrip(new ManagedDragEndPacket { Value=new ManagedDragEnd { Session=Session,ProfileId="corpse",DeathSequence=3 } });
        Check(end.Value.Session == Session,"end carries session identity");
        foreach (ManagedDragStage stage in Enum.GetValues<ManagedDragStage>())
        {
            var status = Roundtrip(new ManagedDragStatusPacket { Value=new ManagedDragStatus { Session=Session,ProfileId="corpse",DeathSequence=3,
                Sequence=20,Stage=stage,GripPoint=new Vector3(4,5,6) } });
            Check(status.Value.Stage == stage && status.Value.GripPoint.y == 5,"status roundtrip: "+stage);
        }
        NetDataWriter bad = new(); bad.Put((byte)1); ManagedDragStartPacket rejected = default;
        try { rejected.Deserialize(new NetDataReader(bad.CopyData())); Check(false,"unknown protocol rejects"); }
        catch (FormatException) { Check(true,"unknown protocol rejects"); }
        bad.Reset(); new ManagedDragInputPacket { Value=new ManagedDragInput { Session=Session,ProfileId="corpse",Sequence=1,Target=new Vector3(float.NaN,0,0) } }.Serialize(bad);
        try { new ManagedDragInputPacket().Deserialize(new NetDataReader(bad.CopyData())); Check(false,"malformed target rejects"); }
        catch (FormatException) { Check(true,"malformed target rejects"); }
    }
    private static void TestClaimsAndQueue()
    {
        Type bridge = typeof(BodyDragFikaBridge); Type claimType = bridge.GetNestedType("ManagedClaim",All);
        IDictionary claims = (IDictionary)bridge.GetField("ManagedClaims",All).GetValue(null);
        object claim = Activator.CreateInstance(claimType,true); object owner=RuntimeHelpers.GetUninitializedObject(typeof(NetPeer)), intruder=RuntimeHelpers.GetUninitializedObject(typeof(NetPeer));
        claimType.GetField("Identity",All).SetValue(claim,new ManagedDragStart { Session=Session,ProfileId="corpse",DeathSequence=3 });
        claimType.GetField("Holder",All).SetValue(claim,owner); claims.Add("corpse",claim);
        bool Own(string session,uint death,object peer) => (bool)Call(bridge,"OwnsManaged",session,"corpse",death,peer);
        Check(Own(Session,3,owner) && !Own(Session,3,intruder) && !Own(Session,3,null),"only authenticated peer may drive/end host claim");
        Check(!Own(new string('b',32),3,owner) && !Own(Session,4,owner),"old session/death cannot release a new claim");
        claimType.GetField("Holder",All).SetValue(claim,null);
        Check(Own(Session,3,null) && !Own(Session,3,owner),"host-local claim ownership is explicit");
        claims.Clear();
        int notifications=0; BodyDragSync.ApplyManagedStatus = _ => notifications++;
        ManagedDragStatus status = new() { Session=Session,ProfileId="corpse",DeathSequence=3,Sequence=1,Stage=ManagedDragStage.Held };
        Call(bridge,"ApplyManagedStatus",status); Call(bridge,"ApplyManagedStatus",status);
        Check(notifications == 1,"duplicate status cannot replay local engagement");
        IDictionary known = (IDictionary)bridge.GetField("KnownManagedClaims",All).GetValue(null);
        ManagedDragStatus stale = status; stale.Session=new string('b',32); stale.Sequence=9; stale.Stage=ManagedDragStage.Closed;
        Call(bridge,"ApplyManagedStatus",stale);
        Check(known.Contains("corpse"),"unrelated terminal status cannot clear current claim");
        status.Sequence=2; status.Stage=ManagedDragStage.Closed; Call(bridge,"ApplyManagedStatus",status);
        Check(!known.Contains("corpse"),"current terminal status releases claim visibility");
        int executed=0; int epoch=(int)bridge.GetField("_networkEpoch",All).GetValue(null);
        Call(bridge,"Enqueue",(Action)(() => executed++),epoch);
        Check(executed == 0,"network callback cannot mutate Unity/API inline");
        Call(bridge,"DrainMainThread"); Check(executed == 1,"plugin main tick drains queued intent");
        Call(bridge,"Enqueue",(Action)(() => executed++),epoch); Call(bridge,"ClearManagedClaims"); Call(bridge,"DrainMainThread");
        Check(executed == 1,"old raid callbacks cannot reach new raid");
    }
    private static void TestArchitecture()
    {
        using AssemblyDefinition main=AssemblyDefinition.ReadAssembly(typeof(BodyDragSync).Assembly.Location);
        Check(!main.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Rupture") || r.Name == "Fika.Core"),"main plugin has no hard optional-mod assembly reference");
        TypeDefinition authority=main.MainModule.GetType("BodyDragging.Integration.ManagedDragAuthority");
        var authorityCalls=authority.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand).OfType<MethodReference>().ToArray();
        Check(!authorityCalls.Any(m=>m.DeclaringType.FullName.Contains("Camera") || m.DeclaringType.FullName.Contains("GamePlayerOwner") ||
            m.DeclaringType.FullName == "UnityEngine.Rigidbody"),"authority physics needs no camera/local player or native rigidbody writes");
        Check(!authority.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand).OfType<FieldReference>().Any(f=>f.Name=="HeadlessApplyFinalPoseOnly"),
            "headless final-pose-only configuration cannot disable intent solver");
        TypeDefinition plugin=main.MainModule.GetType("BodyDragging.Plugin");
        Check(plugin.Methods.Single(m=>m.Name=="Update").Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>().Any(m=>m.DeclaringType.FullName==authority.FullName && m.Name=="Tick"),
            "authority loop runs from plugin Update on every role");
        using AssemblyDefinition bridge=AssemblyDefinition.ReadAssembly(typeof(BodyDragFikaBridge).Assembly.Location);
        var packet=bridge.MainModule.GetType("BodyDragFika.ManagedDragInputPacket");
        Check(packet.Fields.Count==1 && packet.Fields[0].FieldType.Name=="ManagedDragInput" &&
            !main.MainModule.GetType("BodyDragging.Integration.ManagedDragInput").Fields.Any(f=>f.FieldType is ArrayType),"managed input contains no bone poses or force arrays");
        var server=bridge.MainModule.GetType("BodyDragFika.BodyDragFikaBridge");
        var callbacks=server.NestedTypes.SelectMany(t=>t.Methods).Where(m=>m.Name.StartsWith("<RegisterManagedServer>") && m.HasBody && m.Parameters.Count==2).ToArray();
        Check(callbacks.Length==3 && callbacks.All(m=>m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>().Any(call=>call.Name=="Enqueue")),
            "every managed server packet enters the main-thread queue");
        var consumers=server.NestedTypes.SelectMany(t=>t.Methods).Where(m=>m.Name.StartsWith("<RegisterManagedServer>") && m.HasBody && m.Parameters.Count==0)
            .Select(m=>m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>().ToArray()).ToArray();
        Check(consumers.Where(c=>c.Any(m=>m.Name=="Input" || m.Name=="End")).All(c=>c.TakeWhile(m=>m.Name!="Input" && m.Name!="End").Any(m=>m.Name=="OwnsManaged")),
            "queued input and end recheck ownership before touching authority lease");
        var stop=main.MainModule.GetType("BodyDragging.Features.CorpseDragController").Methods.Single(m=>m.Name=="OnDestroy");
        var instructions=stop.Body.Instructions;
        int firstReturn=instructions.ToList().FindIndex(i=>i.OpCode==Mono.Cecil.Cil.OpCodes.Ret);
        Check(firstReturn>0 && !instructions.Take(firstReturn).Select(i=>i.Operand).OfType<MethodReference>().Any(m=>m.DeclaringType.Name=="CorpseRagdollSettlement" || m.Name=="BuildPose"),
            "managed release returns before native settlement and final pose stream");
        int ownership=instructions.ToList().FindIndex(i=>i.Operand is FieldReference f && f.Name=="_nativeOwnershipStarted");
        int settlement=instructions.ToList().FindIndex(i=>i.Operand is MethodReference m && m.DeclaringType.Name=="CorpseRagdollSettlement");
        Check(ownership>=0 && ownership<settlement,"rejected acquisition cannot schedule native settlement");
    }
}

public static class FixtureAbi
{
    public static DragResult Result;
    public static DragBegin BeginRequest;
    public static DragFrame Frame;
    public static ulong EndLease;
    public static DragEndReason EndReason;
    public static DragBodyView[] Bodies = { new() { BodyIndex=0,Eligible=true }, new() { BodyIndex=1,Eligible=false } };
    public static DragResult Inspect(Corpse corpse,out CorpseControlInfo info)
    { info=new() { IsAuthority=true,DeathSequence=3,Capabilities=DragCapabilities.RadialTether|DragCapabilities.BodyAcceleration|DragCapabilities.RigTranslation }; return Result; }
    public static DragResult Begin(Corpse corpse,DragBegin begin,out ulong lease) { BeginRequest=begin; lease=42; return Result; }
    public static ulong Step = 90;
    public static DragResult Read(ulong lease,out DragView view) { view=new() { CompletedStep=Step,SimulationDeltaTime=.1f,TopologyRevision=7,GripBodyIndex=0,Bodies=Bodies }; return Result; }
    public static DragResult Submit(ulong lease,DragFrame frame) { Frame=frame; return Result; }
    public static DragResult End(ulong lease,DragEndReason reason) { EndLease=lease; EndReason=reason; return DragResult.Pending; }
}
