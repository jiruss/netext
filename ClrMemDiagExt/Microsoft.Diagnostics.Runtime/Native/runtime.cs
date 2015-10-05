﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Diagnostics.Runtime.Desktop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Address = System.UInt64;

namespace Microsoft.Diagnostics.Runtime.Native
{
    internal class NativeRuntime : RuntimeBase
    {
        private ISOSNative _sos;
        private NativeHeap _heap;
        private ClrThread[] _threads;
        private NativeModule[] _modules;
        private NativeAppDomain _domain;
        private int _dacRawVersion;

        public NativeRuntime(DataTargetImpl dt, DacLibrary lib)
            : base(dt, lib)
        {
            byte[] tmp = new byte[sizeof(int)];

            if (!Request(DacRequests.VERSION, null, tmp))
                throw new ClrDiagnosticsException("Failed to request dac version.", ClrDiagnosticsException.HR.DacError);

            _dacRawVersion = BitConverter.ToInt32(tmp, 0);
            if (_dacRawVersion != 10 && _dacRawVersion != 11)
                throw new ClrDiagnosticsException("Unsupported dac version.", ClrDiagnosticsException.HR.DacError);
        }

        protected override void InitApi()
        {
            if (_sos == null)
            {
                var dac = _library.DacInterface;
                if (!(dac is ISOSNative))
                    throw new ClrDiagnosticsException("This version of mrt100 is too old.", ClrDiagnosticsException.HR.DataRequestError);

                _sos = (ISOSNative)dac;
            }
        }

        public override ClrHeap GetHeap()
        {
            if (_heap == null)
                _heap = new NativeHeap(this, NativeModules, null);

            return _heap;
        }

        public override ClrHeap GetHeap(System.IO.TextWriter log)
        {
            if (_heap == null)
                _heap = new NativeHeap(this, NativeModules, log);
            else
                _heap.Log = log;

            return _heap;
        }

        public override int PointerSize
        {
            get { return IntPtr.Size; }
        }

        public override IList<ClrAppDomain> AppDomains
        {
            get
            {
                return new ClrAppDomain[] { GetRhAppDomain() };
            }
        }

        public override IList<ClrThread> Threads
        {
            get
            {
                if (_threads == null)
                    InitThreads();

                return _threads;
            }
        }

        public override IEnumerable<ClrHandle> EnumerateHandles()
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<ClrMemoryRegion> EnumerateMemoryRegions()
        {
            throw new NotImplementedException();
        }

        public override ClrMethod GetMethodByAddress(ulong ip)
        {
            throw new NotImplementedException();
        }

        public override void Flush()
        {
            OnRuntimeFlushed();
            throw new NotImplementedException();
        }

        internal override IEnumerable<ClrStackFrame> EnumerateStackFrames(uint osThreadId)
        {
            throw new NotImplementedException();
        }
        override public ClrThreadPool GetThreadPool() { throw new NotImplementedException(); }

        internal ClrAppDomain GetRhAppDomain()
        {
            if (_domain == null)
                _domain = new NativeAppDomain(NativeModules);

            return _domain;
        }

        internal NativeModule[] NativeModules
        {
            get
            {
                if (_modules != null)
                    return _modules;

                List<ModuleInfo> modules = new List<ModuleInfo>(DataTarget.EnumerateModules());
                modules.Sort((x, y) => x.ImageBase.CompareTo(y.ImageBase));

                int count;
                if (_sos.GetModuleList(0, null, out count) < 0)
                {
                    _modules = ConvertModuleList(modules);
                    return _modules;
                }

                Address[] ptrs = new Address[count];
                if (_sos.GetModuleList(count, ptrs, out count) < 0)
                {
                    _modules = ConvertModuleList(modules);
                    return _modules;
                }

                Array.Sort(ptrs);

                int i = 0, j = 0;
                while (i < modules.Count && j < ptrs.Length)
                {
                    ModuleInfo info = modules[i];
                    ulong addr = ptrs[j];
                    if (info.ImageBase <= addr && addr < info.ImageBase + info.FileSize)
                    {
                        i++;
                        j++;
                    }
                    else if (addr < info.ImageBase)
                    {
                        j++;
                    }
                    else if (addr >= info.ImageBase + info.FileSize)
                    {
                        modules.RemoveAt(i);
                    }
                }

                modules.RemoveRange(i, modules.Count - i);
                _modules = ConvertModuleList(modules);
                return _modules;
            }
        }

        private NativeModule[] ConvertModuleList(List<ModuleInfo> modules)
        {
            NativeModule[] result = new NativeModule[modules.Count];

            int i = 0;
            foreach (var module in modules)
                result[i++] = new NativeModule(this, module);

            return result;
        }

        internal unsafe IList<ClrRoot> EnumerateStackRoots(ClrThread thread)
        {
            int contextSize;

            var plat = _dataReader.GetArchitecture();
            if (plat == Architecture.Amd64)
                contextSize = 0x4d0;
            else if (plat == Architecture.X86)
                contextSize = 0x2d0;
            else if (plat == Architecture.Arm)
                contextSize = 0x1a0;
            else
                throw new InvalidOperationException("Unexpected architecture.");

            byte[] context = new byte[contextSize];
            _dataReader.GetThreadContext(thread.OSThreadId, 0, (uint)contextSize, context);

            var walker = new NativeStackRootWalker(GetHeap(), GetRhAppDomain(), thread);
            THREADROOTCALLBACK del = new THREADROOTCALLBACK(walker.Callback);
            IntPtr callback = Marshal.GetFunctionPointerForDelegate(del);

            fixed (byte* b = &context[0])
            {
                IntPtr ctx = new IntPtr(b);
                _sos.TraverseStackRoots(thread.Address, ctx, contextSize, callback, IntPtr.Zero);
            }
            GC.KeepAlive(del);

            return walker.Roots;
        }

        internal IList<ClrRoot> EnumerateStaticRoots(bool resolveStatics)
        {
            var walker = new NativeStaticRootWalker(this, resolveStatics);
            STATICROOTCALLBACK del = new STATICROOTCALLBACK(walker.Callback);
            IntPtr ptr = Marshal.GetFunctionPointerForDelegate(del);
            _sos.TraverseStaticRoots(ptr);
            GC.KeepAlive(del);

            return walker.Roots;
        }

        internal IEnumerable<ClrRoot> EnumerateHandleRoots()
        {
            var walker = new NativeHandleRootWalker(this, _dacRawVersion != 10);
            HANDLECALLBACK callback = new HANDLECALLBACK(walker.RootCallback);
            IntPtr ptr = Marshal.GetFunctionPointerForDelegate(callback);
            _sos.TraverseHandleTable(ptr, IntPtr.Zero);
            GC.KeepAlive(callback);

            return walker.Roots;
        }

        private void InitThreads()
        {
            IThreadStoreData tsData = GetThreadStoreData();
            List<ClrThread> threads = new List<ClrThread>(tsData.Count);

            ulong addr = tsData.FirstThread;
            IThreadData thread = GetThread(tsData.FirstThread);
            for (int i = 0; thread != null; i++)
            {
                threads.Add(new DesktopThread(this, thread, addr, tsData.Finalizer == addr));

                addr = thread.Next;
                thread = GetThread(addr);
            }

            _threads = threads.ToArray();
        }



        internal string ResolveSymbol(ulong eetype)
        {
            return DataTarget.ResolveSymbol(eetype);
        }

        #region Native Implementation

        internal override ClrAppDomain GetAppDomainByAddress(Address addr)
        {
            return _domain;
        }

        internal override ulong GetFirstThread()
        {
            IThreadStoreData tsData = GetThreadStoreData();
            if (tsData == null)
                return 0;
            return tsData.FirstThread;
        }

        internal override IThreadData GetThread(ulong addr)
        {
            if (addr == 0)
                return null;

            NativeThreadData data;
            if (_sos.GetThreadData(addr, out data) < 0)
                return null;

            return data;
        }

        internal override IHeapDetails GetSvrHeapDetails(ulong addr)
        {
            NativeHeapDetails data;
            if (_sos.GetGCHeapDetails(addr, out data) < 0)
                return null;

            return data;
        }

        internal override IHeapDetails GetWksHeapDetails()
        {
            NativeHeapDetails data;
            if (_sos.GetGCHeapStaticData(out data) < 0)
                return null;

            return data;
        }

        internal override ulong[] GetServerHeapList()
        {
            int count = 0;
            if (_sos.GetGCHeapList(0, null, out count) < 0)
                return null;

            ulong[] items = new ulong[count];
            if (_sos.GetGCHeapList(items.Length, items, out count) < 0)
                return null;

            return items;
        }

        internal override IThreadStoreData GetThreadStoreData()
        {
            NativeThreadStoreData data;
            if (_sos.GetThreadStoreData(out data) < 0)
                return null;

            return data;
        }

        internal override ISegmentData GetSegmentData(ulong addr)
        {
            if (addr == 0)
                return null;

            NativeSegementData data;
            if (_sos.GetGCHeapSegment(addr, out data) < 0)
                return null;

            return data;
        }

        internal override IMethodTableData GetMethodTableData(ulong eetype)
        {
            NativeMethodTableData data;
            if (_sos.GetEETypeData(eetype, out data) < 0)
                return null;

            return data;
        }

        internal ulong GetFreeType()
        {
            // Can't return 0 on error here, as that would make values of 0 look like a
            // valid method table.  Instead, return something that won't likely be the value
            // in the methodtable.
            ulong free;
            if (_sos.GetFreeEEType(out free) < 0)
                return ulong.MaxValue - 42;

            return free;
        }

        internal override IGCInfo GetGCInfo()
        {
            LegacyGCInfo info;
            if (_sos.GetGCHeapData(out info) < 0)
                return null;

            return info;
        }
        #endregion

        internal override uint GetTlsSlot()
        {
            throw new NotImplementedException();
        }

        internal override uint GetThreadTypeIndex()
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<int> EnumerateGCThreads()
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<ClrModule> EnumerateModules()
        {
            throw new NotImplementedException();
        }

        public override CcwData GetCcwDataFromAddress(Address addr)
        {
            throw new NotImplementedException();
        }
    }
}
