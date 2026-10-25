using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;

namespace OceanBaseConnector.CollectibleProbe
{
    // A stale collectible TLS handle can corrupt the heap; keep this check outside the test host.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var product = Path.GetFullPath(args[0]);
            var exitThread = args[1] == "exit";
            using var touched = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var old = StartOld(product, touched, release, out var holder);
            for (var i = 0; old.IsAlive && i < 100; i++) Collect();
            Console.WriteLine("Old context collected: " + !old.IsAlive);
            if (old.IsAlive) return 3;

            var context = new AssemblyLoadContext("current", isCollectible: true);
            var probe = context.LoadFromAssemblyPath(typeof(Program).Assembly.Location).GetType(typeof(StateProbe).FullName!)!;
            probe.GetMethod(nameof(StateProbe.TouchAll))!.Invoke(null, null);
            Touch(context.LoadFromAssemblyPath(product));
            var count = probe.GetMethod(nameof(StateProbe.CountBad))!;
            var before = (int)count.Invoke(null, null)!;
            Console.WriteLine("Corrupted statics before thread exit: " + before);
            if (exitThread)
            {
                release.Set();
                if (!holder.Join(TimeSpan.FromSeconds(10))) return 4;
            }
            for (var i = 0; i < 5; i++) Collect();
            var pressure = new object[200_000];
            for (var i = 0; i < pressure.Length; i++) pressure[i] = new byte[48];
            Collect();
            var after = (int)count.Invoke(null, null)!;
            Console.WriteLine("Corrupted statics after GC: " + after);
            GC.KeepAlive(pressure);
            GC.KeepAlive(context);
            // The control's background thread stays alive until the child process exits.
            return before == 0 && after == 0 ? 0 : 2;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference StartOld(string product, ManualResetEventSlim touched, ManualResetEventSlim release, out Thread holder)
        {
            var context = new AssemblyLoadContext("old", isCollectible: true);
            var cell = new Assembly?[] { context.LoadFromAssemblyPath(product) };
            Exception? failure = null;
            holder = new Thread(() =>
            {
                try { TouchAndForget(cell); }
                catch (Exception error) { failure = error; }
                finally { touched.Set(); }
                release.Wait();
            })
            { IsBackground = true };
            holder.Start();
            if (!touched.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Diagnostic thread did not start.");
            if (failure != null) throw new InvalidOperationException("Diagnostic thread failed.", failure);
            var weak = new WeakReference(context);
            context.Unload();
            return weak;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TouchAndForget(Assembly?[] cell)
        {
            Touch(cell[0]!);
            cell[0] = null;
        }

        private static void Touch(Assembly product)
        {
            var type = product.GetType("OceanBaseConnector.Internal.Protocol.ProtocolDiagnostics", throwOnError: true)!;
            var instance = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetMethod("Boundary", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, new object[] { "probe" });
        }

        private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }

    public sealed class Marker { public readonly long Tag = 0x5eed; }

    public static class StateProbe
    {
        // Separate nongeneric GC-static boxes expose a handle released in the wrong loader allocator.
        private static class C0 { internal static readonly object Value = new Marker(); }
        private static class C1 { internal static readonly object Value = new Marker(); }
        private static class C2 { internal static readonly object Value = new Marker(); }
        private static class C3 { internal static readonly object Value = new Marker(); }
        private static class C4 { internal static readonly object Value = new Marker(); }
        private static class C5 { internal static readonly object Value = new Marker(); }
        private static class C6 { internal static readonly object Value = new Marker(); }
        private static class C7 { internal static readonly object Value = new Marker(); }
        private static class C8 { internal static readonly object Value = new Marker(); }
        private static class C9 { internal static readonly object Value = new Marker(); }
        private static class C10 { internal static readonly object Value = new Marker(); }
        private static class C11 { internal static readonly object Value = new Marker(); }
        private static class C12 { internal static readonly object Value = new Marker(); }
        private static class C13 { internal static readonly object Value = new Marker(); }
        private static class C14 { internal static readonly object Value = new Marker(); }
        private static class C15 { internal static readonly object Value = new Marker(); }

        public static void TouchAll()
        {
            _ = C0.Value; _ = C1.Value; _ = C2.Value; _ = C3.Value;
            _ = C4.Value; _ = C5.Value; _ = C6.Value; _ = C7.Value;
            _ = C8.Value; _ = C9.Value; _ = C10.Value; _ = C11.Value;
            _ = C12.Value; _ = C13.Value; _ = C14.Value; _ = C15.Value;
        }

        public static int CountBad()
        {
            var values = new[] { C0.Value, C1.Value, C2.Value, C3.Value, C4.Value, C5.Value, C6.Value, C7.Value,
                C8.Value, C9.Value, C10.Value, C11.Value, C12.Value, C13.Value, C14.Value, C15.Value };
            var bad = 0;
            foreach (var value in values) if (!(value is Marker marker && marker.Tag == 0x5eed)) bad++;
            return bad;
        }
    }
}
