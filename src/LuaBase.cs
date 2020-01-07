using NLua.Extensions;
using System;

namespace NLua {
    /// <summary>
    /// Base class to provide consistent disposal flow across lua objects. Uses code provided by Yves Duhoux and suggestions by Hans Schmeidenbacher and Qingrui Li 
    /// </summary>
    public abstract class LuaBase : IDisposable {
        private bool _Disposed;
        public readonly int Reference;
        public readonly Lua Lua;

        protected LuaBase(int reference, Lua lua) {
            Lua = lua;
            Reference = reference;
        }

        /// <summary>
        /// Pushes the current object onto the Lua stack
        /// </summary>
        protected virtual void Push()
            => Lua.State.GetRef(Reference);
        internal void _Push() => Push();

        ~LuaBase() {
            Dispose(false);
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public virtual void Dispose(bool disposeManagedResources) {
            if (_Disposed)
                return;
            _Disposed = true;
            if (Reference != 0)
                Lua.DisposeInternal(Reference, !disposeManagedResources);
        }

        public override string ToString() {
            string name = GetType().Name;
            return name.Substring(3, 1).ToLowerInvariant() + name.Substring(4);
        }

        public override bool Equals(object o)
            => (o is LuaBase other) && Lua.CompareRef(other.Reference, Reference);

        public override int GetHashCode()
            => Reference;
    }
}
