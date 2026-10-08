using System.Reflection;
using System.Runtime.InteropServices;
using BodyDragging.Integration;
using UnityEngine;

// Uses Rupture's actual managed/native ABI definitions and pinned PhysX binary.
// This is an isolated two-body fixture, not an EFT corpse or live network raid.
internal static class NativeDriveTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static void Run(string backendPath,string bridgePath,Action<bool,string> check)
    {
        Type apiType=Assembly.LoadFrom(Path.GetFullPath(backendPath)).GetType("Rupture.PhysXBackend.NativeApi",true);
        object api=Activator.CreateInstance(apiType,All,null,new object[] {Path.GetDirectoryName(Path.GetFullPath(bridgePath)),false},null);
        object New(string name)
        {
            Type type=apiType.GetNestedType(name,All); object value=Activator.CreateInstance(type);
            type.GetField("StructSize",All)?.SetValue(value,(uint)Marshal.SizeOf(type)); return value;
        }
        void Set(object value,string name,object field) => value.GetType().GetField(name,All).SetValue(value,field);
        object Get(object value,string name) => value.GetType().GetField(name,All).GetValue(value);
        object Vector(Vector3 value) => Activator.CreateInstance(apiType.GetNestedType("Vec3",All),All,null,new object[] {value},null);
        Vector3 ReadVector(object value) => new((float)Get(value,"X"),(float)Get(value,"Y"),(float)Get(value,"Z"));
        object Pose(Vector3 position) => Activator.CreateInstance(apiType.GetNestedType("Pose",All),All,null,new object[] {position,Quaternion.identity},null);
        object Native(string name,params object[] args)
        {
            object result=((Delegate)apiType.GetField(name,All).GetValue(api)).DynamicInvoke(args);
            if (result is int status && status!=0) throw new Exception("Native fixture: "+name+" returned "+status);
            return result;
        }
        try
        {
            foreach (float dt in new[] {1f/60f,1f/30f,.1f,.2f})
            {
                object[] create={New("SceneDesc"),IntPtr.Zero}; Native("CreateScene",create); IntPtr scene=(IntPtr)create[1];
                try
                {
                    ulong Actor(uint kind,float mass,Vector3 position)
                    {
                        object desc=New("BodyDesc"); Set(desc,"Kind",kind); Set(desc,"Pose",Pose(position)); Set(desc,"Mass",mass); Set(desc,"Flags",2u);
                        Set(desc,"SolverPositionIterations",12u); Set(desc,"SolverVelocityIterations",4u); Set(desc,"MaxAngularVelocity",7f);
                        Set(desc,"MaxDepenetrationVelocity",10f); Set(desc,"InertiaTensor",Vector(Vector3.one));
                        Set(desc,"InertiaRotation",Get(Pose(Vector3.zero),"Rotation"));
                        object[] args={scene,desc,0UL}; Native("CreateActor",args); return (ulong)args[2];
                    }
                    ulong hand=Actor(2,1,Vector3.zero),chest=Actor(1,80,Vector3.zero),limb=Actor(1,5,new Vector3(1,0,0));
                    ulong Joint(ulong first,ulong second,float distance)
                    {
                        object desc=New("DistanceJointDesc"); Set(desc,"Actor0",first); Set(desc,"Actor1",second);
                        Set(desc,"LocalPose0",Pose(Vector3.zero)); Set(desc,"LocalPose1",Pose(Vector3.zero)); Set(desc,"MaximumDistance",distance);
                        Set(desc,"Tolerance",.001f); Set(desc,"BreakForce",float.MaxValue); Set(desc,"BreakTorque",float.MaxValue);
                        Set(desc,"MassScale",1f); Set(desc,"ConnectedMassScale",1f);
                        object[] args={scene,desc,0UL}; Native("CreateDistanceJoint",args); return (ulong)args[2];
                    }
                    ulong tether=Joint(hand,chest,.05f),coreJoint=Joint(chest,limb,1);
                    ProviderView view=new() { GripIndex=0,Topology=1,SimulationDeltaTime=dt,Bodies=new[] {
                        new ProviderBody { Index=0,Eligible=true }, new ProviderBody { Index=1,Eligible=true,Position=new Vector3(1,0,0) } } };
                    ManagedDragSettings settings=new() { Slack=.05f,HandSpeed=4.5f,TeleportDistance=2.5f,HoldError=2,LimbSpring=4000,MaxAcceleration=8000 };
                    ManagedDragDrive drive=new(settings,view);
                    float maxSpeed=0,maxGap=0;
                    for (int step=1;step<=(int)(2/dt);step++)
                    {
                        Vector3 target=new(1.5f*dt*step,0,0);
                        ProviderAcceleration[] assist=drive.Compose(view,target);
                        if (drive.TranslationId!=0) throw new Exception("Unexpected relocation during steady pull");
                        Native("SetActorPose",scene,hand,Pose(target),1u);
                        Array commands=Array.CreateInstance(apiType.GetNestedType("ForceCommand",All),assist.Length);
                        for (int i=0;i<assist.Length;i++)
                        {
                            object force=New("ForceCommand"); Set(force,"Operation",1u); Set(force,"Actor",limb); Set(force,"Force",Vector(assist[i].Value));
                            Set(force,"Mode",3u); Set(force,"Wake",1u); commands.SetValue(force,i);
                        }
                        Native("ApplyForces",scene,commands,(uint)commands.Length);
                        Array states=Array.CreateInstance(apiType.GetNestedType("BodyState",All),3);
                        object[] result={scene,dt,states,3u,0u}; Native("Step",result);
                        for (int i=0;i<(uint)result[4];i++)
                        {
                            object state=states.GetValue(i); ulong actor=(ulong)Get(state,"Actor");
                            Vector3 position=ReadVector(Get(Get(state,"Pose"),"Position"));
                            if (actor==hand) view.GripPosition=position;
                            else
                            {
                                int index=actor==chest?0:1;
                                view.Bodies[index].Position=view.Bodies[index].CenterOfMass=position;
                                view.Bodies[index].Velocity=ReadVector(Get(state,"LinearVelocity"));
                                maxSpeed=Math.Max(maxSpeed,view.Bodies[index].Velocity.magnitude);
                            }
                        }
                        maxGap=Math.Max(maxGap,(view.GripPosition-view.Bodies[0].CenterOfMass).magnitude);
                    }
                    check(view.Bodies[0].Position.x>2.8f && maxGap<.08f,"consumer drive pulls actual PhysX chest at dt="+dt);
                    check(maxSpeed<30f,"consumer limb assistance stays bounded in actual PhysX at dt="+dt+", speed="+maxSpeed);
                    Native("DestroyJoint",scene,tether); Native("DestroyActor",scene,hand);
                    Native("DestroyJoint",scene,coreJoint); Native("DestroyActor",scene,chest); Native("DestroyActor",scene,limb);
                    check(true,"consumer fixture releases native resources at dt="+dt);
                }
                finally { Native("DestroyScene",scene); }
            }
        }
        finally { ((IDisposable)api).Dispose(); }
    }
}
