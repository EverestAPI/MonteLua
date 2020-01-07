using System;
using KeraLua;

using LuaState = KeraLua.Lua;

namespace NLua {
    public struct SafeLuaContext : IDisposable {

        public Lua Lua;
        public bool WasExecuting;
        public int OldTop;
        public bool Disposed;

        public SafeLuaContext(Lua lua) {
            Lua = lua;
            WasExecuting = lua.IsExecuting;
            OldTop = lua.State.GetTop();
            Disposed = true;
            lua.IsExecuting = true;
        }

        public void Verify(LuaStatus status) {
            if (status != LuaStatus.OK) {
                Lua.IsExecuting = false;
                Lua.ThrowExceptionFromError(OldTop);
            }
        }

        public void Push(int delta = 1) {
            OldTop += delta;
        }

        public void Dispose() {
            if (Disposed)
                return;
            Disposed = true;
            if (Lua.IsExecuting)
                Lua.IsExecuting = WasExecuting;
            Lua.State.SetTop(OldTop);
        }
    }
}
