﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Squared.Util;
using Squared.Task;
using System.Diagnostics;

namespace Squared.PE {
    public class FunctionNotExportedException : Exception {
        public FunctionNotExportedException (string moduleName, string functionName) 
            : base (String.Format("The function '{1}' is not exported by the module '{0}'.", moduleName, functionName)) {
        }
    }

    public class KernelFunctionDisabler : IDisposable {
        // xor eax, eax; ret 4; nop
        public static readonly byte[] ReplacementBytes = new byte[] {
            0x33, 0xC0, 0xC2, 0x04, 0x00, 0x90
        };

        public readonly Dictionary<Pair<string>, byte[]> DisabledFunctions = new Dictionary<Pair<string>, byte[]>();
        public readonly Process Process;

        public KernelFunctionDisabler (Process process) {
            Process = process;
        }

        public void Dispose () {
            foreach (var key in DisabledFunctions.Keys.ToArray())
                EnableFunction(key.First, key.Second);
        }

        protected IntPtr GetFunctionAddress (string moduleName, string functionName) {
            var hModule = Win32.LoadLibrary(moduleName);
            if (hModule == IntPtr.Zero)
                throw new Exception(String.Format("Module load failed: {0}", moduleName));

            try {
                var procAddress = new IntPtr(Win32.GetProcAddress(hModule, functionName));
                if (procAddress == IntPtr.Zero)
                    throw new FunctionNotExportedException(moduleName, functionName);

                return procAddress;
            } finally {
                Win32.FreeLibrary(hModule);
            }
        }

        protected RemoteMemoryRegion GetFunctionRegion (string moduleName, string functionName, byte[] replacementBytes) {
            var address = GetFunctionAddress(moduleName, functionName);
            return RemoteMemoryRegion.Existing(
                Process, address, (uint)replacementBytes.Length
            );
        }

        public static Finally SuspendProcess (Process process) {
            var suspendedThreads = new HashSet<IntPtr>();

            foreach (ProcessThread thread in process.Threads) {
                var hThread = Win32.OpenThread(ThreadAccessFlags.SuspendResume, false, thread.Id);
                if (hThread != IntPtr.Zero) {
                    suspendedThreads.Add(hThread);
                    Win32.SuspendThread(hThread);
                } else {
                    Console.WriteLine("Could not open thread {0}", thread.Id);
                }
            }

            return Finally.Do(() => {
                foreach (IntPtr hThread in suspendedThreads) {
                    Win32.ResumeThread(hThread);
                    Win32.CloseHandle(hThread);
                }
            });
        }

        public unsafe void DisableFunction (string moduleName, string functionName) {
            ReplaceFunction(moduleName, functionName, ReplacementBytes);
        }

        public unsafe void ReplaceFunction (string moduleName, string functionName, byte[] replacementBytes) {
            var key = new Pair<string>(moduleName, functionName);
            if (DisabledFunctions.ContainsKey(key))
                return;

            try {
                if (Process.HasExited)
                    return;
            } catch {
                return;
            }

            var region = GetFunctionRegion(moduleName, functionName, replacementBytes);
            using (var suspend = SuspendProcess(Process))
            using (var handle = region.OpenHandle(ProcessAccessFlags.VMOperation | ProcessAccessFlags.VMRead | ProcessAccessFlags.VMWrite)) {
                region.Protect(handle, 0, region.Size, MemoryProtection.ReadWrite);
                try {
                    var oldBytes = region.ReadBytes(handle, 0, region.Size);
                    DisabledFunctions[key] = oldBytes;
                    fixed (byte* pReplacement = replacementBytes)
                        region.Write(handle, 0, region.Size, pReplacement);
                } finally {
                    region.Protect(handle, 0, region.Size, MemoryProtection.ExecuteRead);
                }
            }
        }

        public unsafe void EnableFunction (string moduleName, string functionName) {
            var key = new Pair<string>(moduleName, functionName);
            if (!DisabledFunctions.ContainsKey(key))
                return;

            try {
                if (Process.HasExited)
                    return;
            } catch {
                return;
            }

            var oldBytes = DisabledFunctions[key];
            DisabledFunctions.Remove(key);

            var region = GetFunctionRegion(moduleName, functionName, oldBytes);
            using (var suspend = SuspendProcess(Process))
            using (var handle = region.OpenHandle(ProcessAccessFlags.VMOperation | ProcessAccessFlags.VMRead | ProcessAccessFlags.VMWrite)) {
                region.Protect(handle, 0, region.Size, MemoryProtection.ReadWrite);
                try {
                    fixed (byte* pOldBytes = oldBytes)
                        region.Write(handle, 0, region.Size, pOldBytes);
                } finally {
                    region.Protect(handle, 0, region.Size, MemoryProtection.ExecuteRead);
                }
            }
        }
    }
}
