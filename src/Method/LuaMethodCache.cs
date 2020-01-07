using System;
using System.Reflection;
using NLua.Extensions;

namespace NLua.Method {
    /// <summary>
    /// A cached method, kept as the last called method in a LuaMethodWrapper.
    /// </summary>
    class LuaMethodCache {
        public LuaMethodCache() {
            Args = new object[0];
            ArgTypes = new LuaMethodArg[0];
            OutIndices = new int[0];
        }

        private MethodBase _CachedMethod;
        public MethodBase CachedMethod {
            get => _CachedMethod;
            set {
                _CachedMethod = value;
                MethodInfo mi = value as MethodInfo;
                if (mi != null)
                    IsReturnVoid = mi.ReturnType == typeof(void);
            }
        }

        public bool IsReturnVoid;
        public object[] Args;
        public int[] OutIndices;
        public LuaMethodArg[] ArgTypes;
    }
}
