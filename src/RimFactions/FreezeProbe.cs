using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RimFactions
{
    /// system:FreezeProbe — used by the watchdog when the game is stuck: it pauses the game's main thread for a moment,
    /// copies where it is and the top of its stack, lets it go again, and then names the code those addresses belong to
    /// (any mod's or the game's). Taken a few times, this shows which code the freeze is looping in. Windows only;
    /// it only reads, and does nothing unless the game has been stuck for a while.
    public static class FreezeProbe
    {
        static uint mainThreadId;

        /// Called on the main thread.
        public static void NoteMainThread()
        {
            if (mainThreadId == 0) try { mainThreadId = GetCurrentThreadId(); } catch { }
        }

        public static string Sample(int times = 6, int gapMs = 150)
        {
            if (mainThreadId == 0) return "main thread unknown";
            try
            {
                var tops = new List<string>();
                var counts = new Dictionary<string, int>();
                List<string> firstStack = null;
                for (int i = 0; i < times; i++)
                {
                    if (!Grab(out ulong rip, out ulong[] stack)) return "couldn't pause the main thread";
                    tops.Add(Describe(rip) ?? NativeModule(rip));
                    var frames = new List<string>();
                    foreach (var a in stack)
                    {
                        var d = Describe(a);
                        if (d != null && (frames.Count == 0 || frames[frames.Count - 1] != d)) frames.Add(d);
                        if (frames.Count >= 45) break;
                    }
                    foreach (var f in frames.Distinct()) counts[f] = counts.TryGetValue(f, out int c) ? c + 1 : 1;
                    if (firstStack == null) firstStack = frames;
                    System.Threading.Thread.Sleep(gapMs);
                }
                var sb = new StringBuilder();
                sb.Append("running now: ").Append(string.Join(" | ", tops));
                sb.Append("\n  in every sample: ").Append(string.Join(" <- ", (firstStack ?? new List<string>()).Where(f => counts[f] == times).Take(30)));
                sb.Append("\n  first sample's stack: ").Append(string.Join(" <- ", firstStack ?? new List<string>()));
                return sb.ToString();
            }
            catch (Exception e) { return "probe failed: " + e.Message; }
        }

        static bool Grab(out ulong rip, out ulong[] stack)
        {
            rip = 0; stack = new ulong[0];
            var h = OpenThread(0x0002 | 0x0008 | 0x0040, false, mainThreadId); // suspend/resume, get context, query
            if (h == IntPtr.Zero) return false;
            IntPtr raw = Marshal.AllocHGlobal(1232 + 16);
            try
            {
                var ctx = new IntPtr((raw.ToInt64() + 15) & ~15L);
                for (int i = 0; i < 1232; i += 8) Marshal.WriteInt64(ctx, i, 0);
                Marshal.WriteInt32(ctx, 0x30, 0x100003); // CONTEXT_CONTROL | CONTEXT_INTEGER (x64)
                if (SuspendThread(h) == uint.MaxValue) return false;
                ulong rsp;
                try
                {
                    if (!GetThreadContext(h, ctx)) return false;
                    rsp = (ulong)Marshal.ReadInt64(ctx, 0x98);
                    rip = (ulong)Marshal.ReadInt64(ctx, 0xF8);
                    // the stack above rsp, up to the end of its memory region (at most 256 KB), copied while paused
                    if (VirtualQuery(new IntPtr((long)rsp), out var mbi, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) != UIntPtr.Zero)
                    {
                        ulong end = (ulong)mbi.BaseAddress.ToInt64() + (ulong)mbi.RegionSize.ToUInt64();
                        int n = (int)Math.Min((end - rsp) / 8, 32768UL);
                        var buf = new long[n];
                        Marshal.Copy(new IntPtr((long)rsp), buf, 0, n);
                        stack = buf.Select(x => (ulong)x).ToArray();
                    }
                }
                finally { ResumeThread(h); }
                return true;
            }
            finally { Marshal.FreeHGlobal(raw); CloseHandle(h); }
        }

        static readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>();

        /// The game code (any mod's) that this address is inside, or null.
        static string Describe(ulong addr)
        {
            if (addr < 0x10000) return null;
            if (names.TryGetValue(addr, out var known)) return known;
            string name = null;
            try
            {
                var ji = mono_jit_info_table_find(mono_get_root_domain(), new IntPtr((long)addr));
                if (ji != IntPtr.Zero)
                {
                    var m = mono_jit_info_get_method(ji);
                    var s = m == IntPtr.Zero ? IntPtr.Zero : mono_method_full_name(m, 0);
                    name = s == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(s);
                }
            }
            catch { }
            if (names.Count < 50000) names[addr] = name;
            return name;
        }

        static string NativeModule(ulong addr)
        {
            try
            {
                if (GetModuleHandleEx(0x4 | 0x2, new IntPtr((long)addr), out var mod))
                {
                    var sb = new StringBuilder(260);
                    GetModuleFileName(mod, sb, sb.Capacity);
                    return "native " + System.IO.Path.GetFileName(sb.ToString()) + "+0x" + (addr - (ulong)mod.ToInt64()).ToString("X");
                }
            }
            catch { }
            return "native 0x" + addr.ToString("X");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORY_BASIC_INFORMATION { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public UIntPtr RegionSize; public uint State, Protect, Type; }

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern UIntPtr VirtualQuery(IntPtr addr, out MEMORY_BASIC_INFORMATION info, UIntPtr size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetModuleHandleEx(uint flags, IntPtr addr, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int size);
        [DllImport("mono-2.0-bdwgc.dll")] static extern IntPtr mono_get_root_domain();
        [DllImport("mono-2.0-bdwgc.dll")] static extern IntPtr mono_jit_info_table_find(IntPtr domain, IntPtr addr);
        [DllImport("mono-2.0-bdwgc.dll")] static extern IntPtr mono_jit_info_get_method(IntPtr ji);
        [DllImport("mono-2.0-bdwgc.dll")] static extern IntPtr mono_method_full_name(IntPtr method, int signature);
    }
}
